using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

// The recipe side of the shared tag vocabulary. The dedup POLICY lives in TagVocabulary.Canonicalize
// (shared with product tags); these pin that RecipeTagVocabulary applies it to Recipe.Tags with the
// same near-duplicate rule, so the product cloud and the recipe cloud can't fragment differently.
public class RecipeTagVocabularyTests
{
    private static Recipe Recipe(params string[] existingTags) =>
        new() { Name = "Test", Tags = [.. existingTags.Select(t => new RecipeTag { Value = t })] };

    [Fact]
    public void ApplyTags_adds_genuinely_new_tags()
    {
        var recipe = Recipe();
        RecipeTagVocabulary.ApplyTags(recipe, ["Dinner", "Italian"], RecipeTagVocabulary.Seed.ToList());
        Assert.Equal(["Dinner", "Italian"], recipe.Tags.Select(t => t.Value));
    }

    [Fact]
    public void ApplyTags_skips_a_tag_the_recipe_already_carries_case_insensitively()
    {
        var recipe = Recipe("Dinner");
        RecipeTagVocabulary.ApplyTags(recipe, ["dinner", "DINNER"], RecipeTagVocabulary.Seed.ToList());
        Assert.Single(recipe.Tags); // still just the one "Dinner"
    }

    [Fact]
    public void ApplyTags_canonicalizes_a_near_duplicate_to_the_vocabulary_form()
    {
        // "Desserts" is a trailing-plural near-duplicate of the seed's "Dessert" — stored as the canonical.
        var recipe = Recipe();
        RecipeTagVocabulary.ApplyTags(recipe, ["Desserts"], RecipeTagVocabulary.Seed.ToList());
        Assert.Equal(["Dessert"], recipe.Tags.Select(t => t.Value));
    }

    [Fact]
    public void ApplyTags_collapses_variants_within_one_batch()
    {
        var recipe = Recipe();
        RecipeTagVocabulary.ApplyTags(recipe, ["Snack", "snacks", "SNACK"], RecipeTagVocabulary.Seed.ToList());
        Assert.Single(recipe.Tags);
    }

    [Fact]
    public void ApplyTags_ignores_blanks()
    {
        var recipe = Recipe();
        RecipeTagVocabulary.ApplyTags(recipe, ["  ", ""], RecipeTagVocabulary.Seed.ToList());
        Assert.Empty(recipe.Tags);
    }
}
