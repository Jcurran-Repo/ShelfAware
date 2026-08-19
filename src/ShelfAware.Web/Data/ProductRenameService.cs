using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>
/// Renames a product and keeps the name-keyed recipe links intact. <c>RecipeIngredient.MatchedProduct</c>
/// stores the product NAME (grounded at recipe-save time) and drives "recipes that use this", the
/// <c>?uses=</c> filter, and the makeability check — so a rename must re-point those strings or the
/// recipe links silently go stale (the old Products-grid inline rename had exactly that hole).
/// </summary>
public class ProductRenameService(IHouseholdDbFactory dbFactory, IActivityLog activityLog)
{
    public sealed record Result(bool Ok, string Message, int RelinkedIngredients = 0);

    /// <summary>The result of a rename staged on a context: whether it applied, the message, the name it
    /// replaced (for the undo record), and how many recipe links it re-pointed.</summary>
    public sealed record RenameOutcome(bool Ok, string Message, string OldName = "", int RelinkedIngredients = 0);

    public async Task<Result> RenameAsync(int productId, string newName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var outcome = await RenameOnAsync(db, productId, newName, cancellationToken);
        if (!outcome.Ok) return new(false, outcome.Message);

        // Record the undoable rename atomically with it — but only a REAL rename (a no-op "same name"
        // staged nothing and is nothing to undo). ProductRenamedHandler reverses by renaming back through
        // this same RenameOnAsync, so the forward and the undo share one definition of "rename".
        var trimmed = newName.Trim();
        if (!string.Equals(outcome.OldName, trimmed, StringComparison.Ordinal))
        {
            activityLog.Record(db, ActivityKind.ProductRenamed,
                new ProductRenamedPayload(productId, outcome.OldName, trimmed));
            await db.SaveChangesAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
        }
        return new(true, outcome.Message, outcome.RelinkedIngredients);
    }

    /// <summary>THE rename, staged on <paramref name="db"/> (NOT saved) — the one definition both the
    /// public <see cref="RenameAsync"/> (which records + commits) and the undo handler (which renames
    /// back) go through. Static and context-passed so the undo runs inside the activity log's own
    /// transaction and can be Peeked without side effects.</summary>
    public static async Task<RenameOutcome> RenameOnAsync(
        ShelfAwareDbContext db, int productId, string newName, CancellationToken cancellationToken = default)
    {
        var name = newName.Trim();
        if (name.Length == 0) return new(false, "A product name is required.");

        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return new(false, "Product not found.");
        if (string.Equals(product.Name, name, StringComparison.Ordinal)) return new(true, "No change.", product.Name);

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

        return new(true, $"Renamed to {name}.", oldName, linked.Count);
    }
}
