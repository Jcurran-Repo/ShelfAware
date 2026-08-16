using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Core.Recipes;

/// <summary>
/// The curated starter tag vocabulary for RECIPES (meal, cuisine, diet, dish type) plus the apply/dedup
/// path for recipe tags. The dedup POLICY — what counts as a near-duplicate and the canonical form to
/// store — is <see cref="TagVocabulary.Canonicalize"/>, shared with product tags so the two can't drift;
/// only the seed list and the target collection (<see cref="Recipe.Tags"/>) are recipe-specific.
/// </summary>
public static class RecipeTagVocabulary
{
    /// <summary>Starter recipe tags across the axes a cook browses by. Users (and the AI) add more.</summary>
    public static readonly IReadOnlyList<string> Seed =
    [
        "Breakfast", "Lunch", "Dinner", "Dessert", "Snack", "Side", "Drink",
        "Italian", "Mexican", "Asian", "Indian", "Mediterranean", "American",
        "Vegetarian", "Vegan", "Gluten-Free", "Dairy-Free", "Low-Carb", "High-Protein",
        "Quick", "One-Pot", "Slow Cooker", "Grill", "Baking", "Soup", "Salad", "Pasta",
    ];

    /// <summary>
    /// Canonicalize each tag (<see cref="TagVocabulary.Canonicalize"/>) and apply it to the recipe unless
    /// it already carries the tag or a near-duplicate; newly coined tags are added to
    /// <paramref name="vocabulary"/> so later tags in the same batch dedup against them. The ONE
    /// recipe-tag-apply path — shared by auto-tagging on save, the "suggest tags" action, and import.
    /// </summary>
    public static void ApplyTags(Recipe recipe, IReadOnlyList<string> tags, List<string> vocabulary)
    {
        foreach (var raw in tags)
        {
            var canonical = TagVocabulary.Canonicalize(raw, recipe.Tags.Select(t => t.Value).ToList(), vocabulary);
            if (canonical is null) continue;
            recipe.Tags.Add(new RecipeTag { Value = canonical });
            if (!vocabulary.Any(v => string.Equals(v, canonical, StringComparison.OrdinalIgnoreCase)))
                vocabulary.Add(canonical);
        }
    }
}
