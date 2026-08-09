using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;

namespace ShelfAware.Web.Data;

/// <summary>
/// Renames a product and keeps the name-keyed recipe links intact. <c>RecipeIngredient.MatchedProduct</c>
/// stores the product NAME (grounded at recipe-save time) and drives "recipes that use this", the
/// <c>?uses=</c> filter, and the makeability check — so a rename must re-point those strings or the
/// recipe links silently go stale (the old Products-grid inline rename had exactly that hole).
/// </summary>
public class ProductRenameService(IHouseholdDbFactory dbFactory)
{
    public sealed record Result(bool Ok, string Message, int RelinkedIngredients = 0);

    public async Task<Result> RenameAsync(int productId, string newName, CancellationToken cancellationToken = default)
    {
        var name = newName.Trim();
        if (name.Length == 0) return new(false, "A product name is required.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return new(false, "Product not found.");
        if (string.Equals(product.Name, name, StringComparison.Ordinal)) return new(true, "No change.");

        // A rename can't merge two products — matching, aliases, and history all key on distinct rows.
        // (Case-only fixes of the SAME product pass: the check excludes productId itself.)
        // ⚠️ "Taken" is the MATCHER's rule-1 identity, not raw equality: it folds punctuation, so
        // renaming to "Half and Half" beside an existing "Half-and-Half" produced a pair the matcher
        // treats as one product — splitting its history, and jamming every later shelf census on that
        // item with an AmbiguousName refusal it can only escape by picking from the dropdown. One
        // definition of product identity, the same one the census and the add form ask.
        // AsNoTracking: this list is only READ (ExactMatches over it), and tracked entities would ride
        // into SaveChanges' change-tracker diff for no reason.
        var others = await db.Products.AsNoTracking().Where(p => p.Id != productId).ToListAsync(cancellationToken);
        if (ProductMatcher.ExactMatches(name, others) is { Count: > 0 } taken)
            return new(false, $"\"{taken[0].Name}\" already exists — pick a different name (renames can't merge products).");

        var oldName = product.Name;
        product.Name = name;
        // ⚠️ Re-point by the matcher's rule-1 IDENTITY, not ToLower(): a MatchedProduct stored as
        // "Home Canned Sauce" for a product named "Home-Canned Sauce" is the same product to every other
        // guard (line 34 above already uses ExactMatches), so a raw compare here left that link silently
        // stale — the partial conversion this finishes. IdentityKey isn't SQL-translatable, so filter in
        // memory, the same load-then-match shape the collision check above uses.
        var oldKey = ProductMatcher.IdentityKey(oldName);
        var linked = (await db.RecipeIngredients
                .Where(i => i.MatchedProduct != null)
                .ToListAsync(cancellationToken))
            .Where(i => ProductMatcher.IdentityKey(i.MatchedProduct!) == oldKey)
            .ToList();
        foreach (var ingredient in linked) ingredient.MatchedProduct = name;

        await db.SaveChangesAsync(cancellationToken);
        return new(true, $"Renamed to {name}.", linked.Count);
    }
}
