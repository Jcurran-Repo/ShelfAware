using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;

namespace ShelfAware.Web.Data;

/// <summary>
/// THE re-point of name-keyed recipe links when a product's name changes hands.
/// <c>RecipeIngredient.MatchedProduct</c> stores a product NAME (grounded at recipe-save time), so a
/// rename or a merge must move those strings or "recipes that use this", the <c>?uses=</c> filter,
/// and makeability go silently stale. Renames and merges are the only two re-pointers, and both call
/// this — the parity their comments used to assert is structural now.
/// <para>Matches by the matcher's rule-1 IDENTITY, never raw equality: a link stored as "Home Canned
/// Sauce" for a product named "Home-Canned Sauce" is the same product to every other guard.
/// ⚠️ An EMPTY old key — a junk-named product being repaired to a real name, the one name whose key
/// the identity system cannot see — re-points NOTHING rather than dragging every other identity-less
/// link onto the new name (one empty key is the whole equivalence class, and no legitimate link keys
/// on ""). IdentityKey isn't SQL-translatable, so this filters in memory, the load-then-match shape
/// the rename's collision check uses. Stages on the caller's context; never saves.</para>
/// </summary>
public static class RecipeLinks
{
    /// <summary>Re-points every link whose identity is <paramref name="oldName"/>'s onto
    /// <paramref name="newName"/>; returns how many moved.</summary>
    public static async Task<int> RepointAsync(
        ShelfAwareDbContext db, string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var oldKey = ProductMatcher.IdentityKey(oldName);
        if (oldKey.Length == 0) return 0;

        var linked = (await db.RecipeIngredients
                .Where(i => i.MatchedProduct != null)
                .ToListAsync(cancellationToken))
            .Where(i => ProductMatcher.IdentityKey(i.MatchedProduct!) == oldKey)
            .ToList();
        foreach (var ingredient in linked) ingredient.MatchedProduct = newName;
        return linked.Count;
    }
}
