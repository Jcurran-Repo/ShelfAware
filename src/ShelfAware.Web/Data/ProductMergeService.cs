using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>
/// Folds one product into another: every purchase, receipt line, alias, and signal moves to the
/// target, tags and substitutes union in, name-keyed recipe links re-point (the rename rule), and
/// the source row is deleted. This is the repair tool the Variety feature needs for history:
/// extraction used to keep flavor words in the item name, so "Strawberry Drink Mix" and "Grape
/// Drink Mix" exist as separate products that can never roll up on their own — merging them into
/// "Drink Mix" pools their cadence, and <paramref name="varietyForMoved"/> labels the moved
/// purchases with the flavor the old name carried (only where a variety isn't already recorded).
///
/// Order matters: the child tables are re-pointed with immediate <c>ExecuteUpdate</c> SQL before
/// the delete, all inside one transaction — purchases/signals/tags cascade on product delete and
/// ReceiptLine.ProductId has no delete action at all (see ProductDeletionTests), so deleting first
/// would destroy the history the merge exists to combine. Tenancy: every query here runs through
/// the household-scoped context, so a foreign household's product id simply fails to load.
/// <para>The merge captures an undo MANIFEST (see <c>ProductsMergedHandler</c>) — the ids of every
/// row it moves, the source's full definition, and what it added to the target — so it is reversible:
/// the /history undo rebuilds the source and pulls its contribution back out of the survivor.</para>
/// <para>A merge can also give the survivor a NEW name (<c>renameTo</c>) — the lookalike card's "neither
/// name, call it this" choice. The rename runs through <see cref="ProductRenameService.RenameOnAsync"/>, the
/// one definition of a rename (its identity-taken refusal and recipe re-point included), inside the same
/// transaction, and rides on the SAME undo entry: one action, one ↩ Undo that puts both products and both
/// names back. A refused rename rolls the whole merge back, so the user is never left with half of it.</para>
/// </summary>
public class ProductMergeService(IHouseholdDbFactory dbFactory, IActivityLog activityLog)
{
    /// <summary><see cref="Summary"/> is the undo entry's own line ("Merged A into B", "Merged A and B as C") —
    /// the one sentence for what this merge did, so an inline notice says exactly what /history will.</summary>
    public sealed record Result(
        bool Ok, string Message, int MovedPurchases = 0, int RelinkedIngredients = 0, int UndoEntryId = 0,
        string Summary = "");

    public async Task<Result> MergeAsync(
        int sourceId, int targetId, string? varietyForMoved = null, string? renameTo = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId) return new(false, "A product can't be merged into itself.");
        var variety = string.IsNullOrWhiteSpace(varietyForMoved) ? null : varietyForMoved.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Products.FindAsync([sourceId], cancellationToken);
        var target = await db.Products.FindAsync([targetId], cancellationToken);
        if (source is null || target is null) return new(false, "Product not found.");
        // A blank or unchanged name is no rename — the merge keeps the target's name exactly as before.
        var newName = renameTo?.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, target.Name, StringComparison.Ordinal)) newName = null;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Capture the merge MANIFEST before the fold — the exact rows about to move (with the variety each
        // carried, so the undo knows which the label stamps). The source's own tag/substitute values and the
        // target's pre-merge tracked flag / unit are captured below, as those columns change. This is what
        // makes the merge reversible: the undo rebuilds the source from it and pulls its rows back out.
        var purchaseRows = await db.PurchaseEvents.Where(x => x.ProductId == sourceId)
            .Select(x => new { x.Id, x.Variety }).ToListAsync(cancellationToken);
        var lineRows = await db.ReceiptLines.Where(x => x.ProductId == sourceId)
            .Select(x => new { x.Id, x.Variety }).ToListAsync(cancellationToken);
        var aliasIds = await db.ProductAliases.Where(a => a.ProductId == sourceId)
            .Select(a => a.Id).ToListAsync(cancellationToken);
        var signalIds = await db.InventorySignals.Where(x => x.ProductId == sourceId)
            .Select(x => x.Id).ToListAsync(cancellationToken);

        // Bulk re-points run as immediate SQL through the household query filter. The variety label
        // fills only blanks — a purchase that already knows its flavor keeps it.
        await db.PurchaseEvents
            .Where(p => p.ProductId == sourceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ProductId, targetId)
                .SetProperty(p => p.Variety, p => p.Variety ?? variety), cancellationToken);
        await db.ReceiptLines
            .Where(l => l.ProductId == sourceId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ProductId, (int?)targetId)
                .SetProperty(l => l.Variety, l => l.Variety ?? variety), cancellationToken);
        await db.ProductAliases
            .Where(a => a.ProductId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProductId, targetId), cancellationToken);
        await db.InventorySignals
            .Where(x => x.ProductId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProductId, targetId), cancellationToken);

        var movedPurchaseIds = purchaseRows.Select(r => r.Id).ToList();
        var movedLineIds = lineRows.Select(r => r.Id).ToList();
        // The COALESCE above stamps Variety only where it was blank AND a label was given, so the undo nulls
        // exactly those rows. No label ⇒ nothing was stamped ⇒ nothing to un-stamp.
        var stampedPurchaseIds = variety is null
            ? new List<int>()
            : purchaseRows.Where(r => r.Variety is null).Select(r => r.Id).ToList();
        var stampedLineIds = variety is null
            ? new List<int>()
            : lineRows.Where(r => r.Variety is null).Select(r => r.Id).ToList();

        // Tags and substitutes union in by value; a duplicate row is dropped rather than moved. Capture the
        // source's full set (to recreate on undo) and the subset actually ADDED to the target (to pull off).
        var sourceTags = await db.ProductTags.Where(t => t.ProductId == sourceId).ToListAsync(cancellationToken);
        var sourceTagValues = sourceTags.Select(t => t.Value).ToList();
        var targetTags = await db.ProductTags.Where(t => t.ProductId == targetId)
            .Select(t => t.Value).ToListAsync(cancellationToken);
        var addedTags = new List<string>();
        foreach (var tag in sourceTags)
        {
            if (targetTags.Contains(tag.Value, StringComparer.OrdinalIgnoreCase)) db.ProductTags.Remove(tag);
            else { tag.ProductId = targetId; addedTags.Add(tag.Value); }
        }
        var sourceSubs = await db.ProductSubstitutes.Where(s => s.ProductId == sourceId).ToListAsync(cancellationToken);
        var sourceSubValues = sourceSubs.Select(s => s.Value).ToList();
        var targetSubs = await db.ProductSubstitutes.Where(s => s.ProductId == targetId)
            .Select(s => s.Value).ToListAsync(cancellationToken);
        var addedSubs = new List<string>();
        foreach (var sub in sourceSubs)
        {
            if (targetSubs.Contains(sub.Value, StringComparer.OrdinalIgnoreCase)) db.ProductSubstitutes.Remove(sub);
            else { sub.ProductId = targetId; addedSubs.Add(sub.Value); }
        }

        // Recipe links key on the product NAME, so they follow the merge — via RecipeLinks, the ONE
        // re-pointer shared with the rename service (identity-keyed and empty-key-gated there), so the
        // two sites can't drift apart again. A stale link is worse here than on a rename: the source
        // delete below would ORPHAN it. The ids it moves are captured so the undo can move them back.
        var relinkedIds = await RecipeLinks.RepointAsync(db, source.Name, target.Name, cancellationToken);

        // The merged product is tracked if either half was, and keeps the target's identity otherwise.
        var targetTrackedBefore = target.IsTracked;
        var targetUnitBefore = target.DefaultUnit;
        target.IsTracked |= source.IsTracked;
        target.DefaultUnit ??= source.DefaultUnit;

        // The source goes BEFORE any rename: the rename's taken-check reads the products table, and the source
        // is exactly the name a user may want to keep under a cleaned-up spelling ("Half & Half" over both
        // "Half and Half" and "Half-and-Half") — it must not count as the twin that blocks it. Still inside
        // the transaction, so a refused rename below rolls this delete back with everything else.
        var sourceName = source.Name;
        var sourceSnapshot = new MergedSourceSnapshot(
            source.Category, source.DefaultUnit, source.IsTracked, source.TrackQuantity,
            source.QuantityOnHand, source.QuantityCountedAt, source.CreatedByReceiptId,
            sourceTagValues, sourceSubValues);
        db.Products.Remove(source);
        await db.SaveChangesAsync(cancellationToken);

        string? targetNameBefore = null;
        if (newName is not null)
        {
            var renamed = await ProductRenameService.RenameOnAsync(db, targetId, newName, cancellationToken);
            // tx disposes uncommitted ⇒ the merge rolls back. A taken name is reworded for THIS action: the
            // rename's own "(renames can't merge products)" reads as a contradiction mid-merge.
            if (!renamed.Ok)
                return new(false, renamed.TakenBy is { } taken
                    ? $"\"{taken}\" is already another product — pick a different name, or merge with it from its own page."
                    : renamed.Message);
            targetNameBefore = renamed.OldName;
        }

        // The undo record, staged inside the merge's transaction so it commits with the merge (the source's
        // fields were read above, before the row went). Reversible — the manifest lets the undo rebuild the
        // source, pull its contribution back out of the survivor, and give the survivor its old name back.
        var payload = new ProductsMergedPayload(
            sourceName, target.Name, targetId,
            sourceSnapshot,
            movedPurchaseIds, stampedPurchaseIds, movedLineIds, stampedLineIds,
            aliasIds, signalIds, addedTags, addedSubs, relinkedIds,
            targetTrackedBefore, target.IsTracked, targetUnitBefore, target.DefaultUnit,
            targetNameBefore);
        var entry = activityLog.Record(db, ActivityKind.ProductsMerged, payload);

        await db.SaveChangesAsync(cancellationToken); // assigns entry.Id — returned for the inline ↩ Undo
        await tx.CommitAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);

        var movedCount = movedPurchaseIds.Count;
        return new(true, $"Merged into {target.Name}: {movedCount} purchase{(movedCount == 1 ? "" : "s")} moved.",
            movedCount, relinkedIds.Count, entry.Id, entry.Summary);
    }

    /// <summary>
    /// Suggest the variety label a merge should stamp on the moved purchases: the words of the source
    /// name that the target name doesn't have ("Strawberry Drink Mix" into "Drink Mix" → "Strawberry").
    /// Null when the names share nothing — then the leftover is the whole name, not a flavor. Purely a
    /// pre-fill; the user edits or clears it in the merge panel. Plurals fold so "Gala Apples" into
    /// "Apple" still isolates "Gala".
    /// </summary>
    public static string? SuggestVarietyLabel(string sourceName, string targetName)
    {
        var sourceTokens = Tokenize(sourceName);
        var targetKeys = Tokenize(targetName).Select(Fold).ToHashSet();
        if (sourceTokens.Count == 0 || targetKeys.Count == 0) return null;

        var leftover = sourceTokens.Where(t => !targetKeys.Contains(Fold(t))).ToList();
        if (leftover.Count == 0 || leftover.Count == sourceTokens.Count) return null;
        return string.Join(' ', leftover);
    }

    /// <summary>
    /// Suggest a name for two look-alikes merged under a new one: the longest run of consecutive words both
    /// names share ("Artesano Brioche Bread" + "Brioche Bread Loaf" → "Brioche Bread"), spelled as in
    /// <paramref name="first"/>. Null when they share no word. Purely a pre-fill for the lookalike card's
    /// name field; the user edits it. Plurals fold the same way as <see cref="SuggestVarietyLabel"/>.
    /// </summary>
    public static string? SuggestSharedName(string first, string second)
    {
        var a = Tokenize(first);
        var aKeys = a.Select(Fold).ToList();
        var bKeys = Tokenize(second).Select(Fold).ToList();

        int bestStart = 0, bestLength = 0;
        for (var i = 0; i < aKeys.Count; i++)
            for (var j = 0; j < bKeys.Count; j++)
            {
                var length = 0;
                while (i + length < aKeys.Count && j + length < bKeys.Count && aKeys[i + length] == bKeys[j + length])
                    length++;
                if (length > bestLength) (bestStart, bestLength) = (i, length);
            }
        return bestLength == 0 ? null : string.Join(' ', a.Skip(bestStart).Take(bestLength));
    }

    private static List<string> Tokenize(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    // The same trailing-s plural fold the extraction scorer uses (not -ss), applied to BOTH sides of
    // the comparison only — the suggested label keeps the source's original spelling.
    private static string Fold(string token)
    {
        var lower = token.ToLowerInvariant();
        return lower.Length >= 4 && lower.EndsWith('s') && !lower.EndsWith("ss") ? lower[..^1] : lower;
    }
}
