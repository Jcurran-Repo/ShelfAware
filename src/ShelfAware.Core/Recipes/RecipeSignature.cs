using ShelfAware.Core.Chat;

namespace ShelfAware.Core.Recipes;

/// <summary>
/// The identity of a generated recipe for library DEDUP: its normalized name plus its set of main-ingredient
/// foods. Regenerating a meal plan keeps every recipe as a reusable library (§ meal-planning idea #1), so the
/// same dish produced twice must map to ONE recipe rather than piling up twins. Two recipes share a signature
/// when they have the same name (punctuation/case folded, via <see cref="ProductMatcher.IdentityKey"/>) AND
/// the same main-ingredient foods (core words, order-independent, via <see cref="IngredientMatcher"/>) — so
/// "Skillet Beef Tacos" regenerated identically dedupes, while two genuinely different dishes don't merge.
/// Pure + unit-tested; the ONE definition of "the same generated recipe", used by the meal-plan persister.
/// </summary>
public static class RecipeSignature
{
    public static string Of(string? name, IEnumerable<string> mainIngredientNames)
    {
        var nameKey = ProductMatcher.IdentityKey(name ?? "");
        var ingredientKeys = mainIngredientNames
            .Select(i => string.Join(" ", IngredientMatcher.CoreTokens(i).OrderBy(t => t, StringComparer.Ordinal)))
            .Where(k => k.Length > 0)
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal);
        return nameKey + "|" + string.Join(",", ingredientKeys);
    }
}
