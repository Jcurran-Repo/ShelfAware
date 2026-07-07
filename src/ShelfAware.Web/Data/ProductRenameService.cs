using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Data;

/// <summary>
/// Renames a product and keeps the name-keyed recipe links intact. <c>RecipeIngredient.MatchedProduct</c>
/// stores the product NAME (grounded at recipe-save time) and drives "recipes that use this", the
/// <c>?uses=</c> filter, and the makeability check — so a rename must re-point those strings or the
/// recipe links silently go stale (the old Products-grid inline rename had exactly that hole).
/// </summary>
public class ProductRenameService(IDbContextFactory<ShelfAwareDbContext> dbFactory)
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
        var taken = await db.Products
            .AnyAsync(p => p.Id != productId && p.Name.ToLower() == name.ToLower(), cancellationToken);
        if (taken) return new(false, $"\"{name}\" already exists — pick a different name (renames can't merge products).");

        var oldName = product.Name;
        product.Name = name;
        var linked = await db.RecipeIngredients
            .Where(i => i.MatchedProduct != null && i.MatchedProduct.ToLower() == oldName.ToLower())
            .ToListAsync(cancellationToken);
        foreach (var ingredient in linked) ingredient.MatchedProduct = name;

        await db.SaveChangesAsync(cancellationToken);
        return new(true, $"Renamed to {name}.", linked.Count);
    }
}
