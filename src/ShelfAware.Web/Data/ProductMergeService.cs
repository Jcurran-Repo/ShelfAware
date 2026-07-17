using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

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
/// </summary>
public class ProductMergeService(IHouseholdDbFactory dbFactory)
{
    public sealed record Result(bool Ok, string Message, int MovedPurchases = 0, int RelinkedIngredients = 0);

    public async Task<Result> MergeAsync(
        int sourceId, int targetId, string? varietyForMoved = null, CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId) return new(false, "A product can't be merged into itself.");
        var variety = string.IsNullOrWhiteSpace(varietyForMoved) ? null : varietyForMoved.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Products.FindAsync([sourceId], cancellationToken);
        var target = await db.Products.FindAsync([targetId], cancellationToken);
        if (source is null || target is null) return new(false, "Product not found.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Bulk re-points run as immediate SQL through the household query filter. The variety label
        // fills only blanks — a purchase that already knows its flavor keeps it.
        var movedPurchases = await db.PurchaseEvents
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

        // Tags and substitutes union in by value; a duplicate row is dropped rather than moved.
        var targetTags = await db.ProductTags.Where(t => t.ProductId == targetId)
            .Select(t => t.Value).ToListAsync(cancellationToken);
        foreach (var tag in await db.ProductTags.Where(t => t.ProductId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetTags.Contains(tag.Value, StringComparer.OrdinalIgnoreCase)) db.ProductTags.Remove(tag);
            else tag.ProductId = targetId;
        }
        var targetSubs = await db.ProductSubstitutes.Where(s => s.ProductId == targetId)
            .Select(s => s.Value).ToListAsync(cancellationToken);
        foreach (var sub in await db.ProductSubstitutes.Where(s => s.ProductId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetSubs.Contains(sub.Value, StringComparer.OrdinalIgnoreCase)) db.ProductSubstitutes.Remove(sub);
            else sub.ProductId = targetId;
        }

        // Same rule as ProductRenameService: recipe links key on the product NAME, so they follow it.
        var linked = await db.RecipeIngredients
            .Where(i => i.MatchedProduct != null && i.MatchedProduct.ToLower() == source.Name.ToLower())
            .ToListAsync(cancellationToken);
        foreach (var ingredient in linked) ingredient.MatchedProduct = target.Name;

        // The merged product is tracked if either half was, and keeps the target's identity otherwise.
        target.IsTracked |= source.IsTracked;
        target.DefaultUnit ??= source.DefaultUnit;

        db.Products.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new(true, $"Merged into {target.Name}: {movedPurchases} purchase{(movedPurchases == 1 ? "" : "s")} moved.",
            movedPurchases, linked.Count);
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
