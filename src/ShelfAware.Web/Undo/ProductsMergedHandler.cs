using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>The full manifest a product merge captures so it can be UNDONE — not just the names for the
/// history line, but the exact rows it moved and everything it collapsed. A merge is lossy by nature: it
/// folds one product's purchases / lines / aliases / signals into another, unions tags + substitutes
/// (dropping the duplicates), re-points name-keyed recipe links, flips the target's tracked flag / unit, and
/// DELETES the source — so once it runs, nothing on the survivor distinguishes the moved rows from its own.
/// This manifest, captured before the fold, is what lets the undo pull exactly the source's contribution
/// back out and rebuild the deleted product; without it, merge could only ever be history-only.</summary>
public sealed record ProductsMergedPayload(
    string SourceName,
    string TargetName,
    int TargetId,
    MergedSourceSnapshot Source,
    IReadOnlyList<int> MovedPurchaseIds,
    IReadOnlyList<int> StampedPurchaseIds,
    IReadOnlyList<int> MovedLineIds,
    IReadOnlyList<int> StampedLineIds,
    IReadOnlyList<int> MovedAliasIds,
    IReadOnlyList<int> MovedSignalIds,
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> AddedSubstitutes,
    IReadOnlyList<int> RelinkedIngredientIds,
    bool TargetTrackedBefore,
    bool TargetTrackedAfter,
    string? TargetUnitBefore,
    string? TargetUnitAfter);

/// <summary>Everything needed to recreate the deleted source product as a fresh row (a NEW id — the old one
/// is dead). Its name rides on <see cref="ProductsMergedPayload.SourceName"/> (also used for the summary and
/// the recipe re-point), so it isn't repeated here.</summary>
public sealed record MergedSourceSnapshot(
    Category Category,
    string? DefaultUnit,
    bool IsTracked,
    bool TrackQuantity,
    decimal? QuantityOnHand,
    DateTimeOffset? QuantityCountedAt,
    int? CreatedByReceiptId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Substitutes);

/// <summary>Undo for a product merge: rebuild the deleted source and pull its contribution back out of the
/// survivor — precondition-checked and TOLERANT (revive what still exists; a row deleted since, e.g. by a
/// receipt removal, is genuinely gone and simply skipped — the friendly-egg call). Everything is staged as
/// tracked entities for the one SaveChanges <c>ActivityLogService</c> commits — NO <c>ExecuteUpdate</c> (it
/// autocommits immediately and would break the single-transaction guarantee a refused undo relies on), and
/// the moved rows re-parent onto the recreated source by NAVIGATION, so EF assigns the new id and fixes
/// every FK in that one save.
///
/// <para><see cref="UndoResult.Gone"/> when the survivor is no longer here (deleted or itself merged away —
/// there is nothing to un-merge from); <see cref="UndoResult.Superseded"/> when reviving would collide with a
/// product that now answers to the source's identity (a new one added, or the target renamed onto it) — the
/// twin the merge existed to remove.</para>
///
/// <para>Legacy note: merges recorded before this became reversible carry <c>Reversibility.NotReversible</c>
/// on their <c>ActivityEntry</c> row, so <c>ActivityLogService</c> refuses them before dispatch and
/// <see cref="Reverse"/> never sees their old two-field payload.</para></summary>
public sealed class ProductsMergedHandler : UndoHandler<ProductsMergedPayload>
{
    public override ActivityKind Kind => ActivityKind.ProductsMerged;

    protected override string Summarize(ProductsMergedPayload p) => $"Merged {p.SourceName} into {p.TargetName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, ProductsMergedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        // The survivor must still be here to un-merge from. If it was deleted or itself merged away since,
        // the merge's result is gone — there is nothing to pull the source back out of.
        var target = await db.Products.FindAsync([p.TargetId], ct);
        if (target is null) return UndoResult.Gone;

        // Reviving recreates a product with the source's name. If some product now answers to that identity
        // (a new one added, or the target renamed onto it), reviving would reintroduce the very twin the
        // merge removed — refuse rather than split the item's history again. Identity-keyed, the one
        // definition the matcher, census and add form all ask; an empty key (a junk name) matches nothing.
        var sourceKey = ProductMatcher.IdentityKey(p.SourceName);
        if (sourceKey.Length > 0)
        {
            var names = await db.Products.AsNoTracking().Select(x => x.Name).ToListAsync(ct);
            if (names.Any(n => ProductMatcher.IdentityKey(n) == sourceKey)) return UndoResult.Superseded;
        }

        // Recreate the source as a fresh row. Its tags/substitutes are re-created as its own children, and
        // the moved rows below re-parent onto it by navigation, so EF assigns its id and every FK in one save.
        var source = new Product
        {
            Name = p.SourceName,
            Category = p.Source.Category,
            DefaultUnit = p.Source.DefaultUnit,
            IsTracked = p.Source.IsTracked,
            TrackQuantity = p.Source.TrackQuantity,
            QuantityOnHand = p.Source.QuantityOnHand,
            QuantityCountedAt = p.Source.QuantityCountedAt,
            CreatedByReceiptId = p.Source.CreatedByReceiptId,
            Tags = [.. p.Source.Tags.Select(v => new ProductTag { Value = v })],
            Substitutes = [.. p.Source.Substitutes.Select(v => new ProductSubstitute { Value = v })],
        };
        db.Products.Add(source);

        // Move back exactly the rows this merge moved — but only those STILL on the target (a row deleted or
        // moved away since is genuinely gone; revive what we can). Re-parent by navigation so the FK follows
        // onto the recreated source, and un-stamp the variety the merge wrote (null → label) on exactly the
        // rows it stamped; rows it left alone keep the variety they already had.
        var stampedPurchases = p.StampedPurchaseIds.ToHashSet();
        var movedPurchases = await db.PurchaseEvents
            .Where(x => p.MovedPurchaseIds.Contains(x.Id) && x.ProductId == target.Id).ToListAsync(ct);
        foreach (var pe in movedPurchases)
        {
            pe.Product = source;
            if (stampedPurchases.Contains(pe.Id)) pe.Variety = null;
        }

        var stampedLines = p.StampedLineIds.ToHashSet();
        var movedLines = await db.ReceiptLines
            .Where(x => p.MovedLineIds.Contains(x.Id) && x.ProductId == target.Id).ToListAsync(ct);
        foreach (var l in movedLines)
        {
            l.Product = source;
            if (stampedLines.Contains(l.Id)) l.Variety = null;
        }

        var movedAliases = await db.ProductAliases
            .Where(x => p.MovedAliasIds.Contains(x.Id) && x.ProductId == target.Id).ToListAsync(ct);
        foreach (var a in movedAliases) a.Product = source;

        var movedSignals = await db.InventorySignals
            .Where(x => p.MovedSignalIds.Contains(x.Id) && x.ProductId == target.Id).ToListAsync(ct);
        foreach (var s in movedSignals) s.Product = source;

        // Pull the tags/subs the merge ADDED to the target back off (the ones it merely deduped stay — the
        // target already had those; the recreated source got its full original set above).
        var addedTags = p.AddedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (addedTags.Count > 0)
        {
            var targetTags = await db.ProductTags.Where(t => t.ProductId == target.Id).ToListAsync(ct);
            db.ProductTags.RemoveRange(targetTags.Where(t => addedTags.Contains(t.Value)));
        }
        var addedSubs = p.AddedSubstitutes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (addedSubs.Count > 0)
        {
            var targetSubs = await db.ProductSubstitutes.Where(s => s.ProductId == target.Id).ToListAsync(ct);
            db.ProductSubstitutes.RemoveRange(targetSubs.Where(s => addedSubs.Contains(s.Value)));
        }

        // Re-point the recipe links the merge moved, back to the source's name — but only the ones still
        // pointing at the target's identity (one re-pointed elsewhere by a later rename/merge is left alone).
        if (p.RelinkedIngredientIds.Count > 0)
        {
            var targetKey = ProductMatcher.IdentityKey(target.Name);
            var relinked = await db.RecipeIngredients
                .Where(i => p.RelinkedIngredientIds.Contains(i.Id) && i.MatchedProduct != null).ToListAsync(ct);
            foreach (var ing in relinked)
                if (ProductMatcher.IdentityKey(ing.MatchedProduct!) == targetKey) ing.MatchedProduct = p.SourceName;
        }

        // Restore the target's tracked flag / default unit the merge changed — but only if nothing has
        // changed them since. The merge only ever sets IsTracked true and fills a null unit, so a later user
        // choice (still == the value the merge left) is what we compare against; if they touched it, theirs wins.
        if (target.IsTracked == p.TargetTrackedAfter) target.IsTracked = p.TargetTrackedBefore;
        if (target.DefaultUnit == p.TargetUnitAfter) target.DefaultUnit = p.TargetUnitBefore;

        return UndoResult.Done;
    }
}
