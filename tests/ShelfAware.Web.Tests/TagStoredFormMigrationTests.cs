using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The one-off normalize-and-rewrite pass over the tag columns. Runs against real SQLite because every
/// claim is about rows in a table — an UPDATE, a DELETE by id, and a transaction around both — and
/// because the rows it exists for were written RAW, straight into the column before the cap existed, which
/// is how they are seeded here: through EF with no Canonicalize in the way.
/// </summary>
public class TagStoredFormMigrationTests : IDisposable
{
    private const string A = "hh-a";
    private const string B = "hh-b";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ShelfAwareDbContext As(string household)
    {
        _db.HouseholdId = household;
        return _db.CreateDbContext();
    }

    private async Task<int> SeedProductAsync(string household, string name)
    {
        await using var db = As(household);
        var product = new Product { Name = name };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<int> SeedRecipeAsync(string household, string name)
    {
        await using var db = As(household);
        var recipe = new Recipe { Name = name };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    /// <summary>One tag per save, so ids — which is what "earlier" means to the pass — follow the order
    /// the test wrote them in.</summary>
    private async Task<int> AddProductTagAsync(string household, int productId, string value)
    {
        await using var db = As(household);
        var tag = new ProductTag { ProductId = productId, Value = value };
        db.ProductTags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    private async Task<int> AddRecipeTagAsync(string household, int recipeId, string value)
    {
        await using var db = As(household);
        var tag = new RecipeTag { RecipeId = recipeId, Value = value };
        db.RecipeTags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    private async Task<List<(int Id, string Value)>> ProductTagsAsync(string household, int productId)
    {
        await using var db = As(household);
        return (await db.ProductTags.Where(t => t.ProductId == productId).OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Value }).ToListAsync()).Select(t => (t.Id, t.Value)).ToList();
    }

    private async Task<List<string>> RecipeTagsAsync(string household, int recipeId)
    {
        await using var db = As(household);
        return await db.RecipeTags.Where(t => t.RecipeId == recipeId).OrderBy(t => t.Id).Select(t => t.Value).ToListAsync();
    }

    /// <summary>As boot runs it: on an UNSCOPED context, outside any household.</summary>
    private TagStoredFormMigration.Outcome Boot()
    {
        using var db = _db.CreateUnscopedContext();
        return TagStoredFormMigration.Apply(db, NullLogger.Instance);
    }

    [Fact]
    public async Task A_decomposed_tag_over_the_cap_is_rewritten_composed_and_inside_it()
    {
        // 54 decomposed "é": 108 characters raw, 54 composed — over the cap to FindNearDuplicate's raw
        // measure and inside it to anyone reading it, so the household's own dedup could not see it.
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", TagVocabulary.MaxLength - 10));
        var composed = decomposed.Normalize();
        Assert.True(TagVocabulary.IsOverLength(decomposed));
        Assert.Null(TagVocabulary.FindNearDuplicate(composed, [decomposed])); // the defect, before
        var product = await SeedProductAsync(A, "Creme Fraiche");
        await AddProductTagAsync(A, product, decomposed);

        var outcome = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(Rewritten: 1, Collapsed: 0, LeftAlone: 0), outcome);
        var (_, stored) = Assert.Single(await ProductTagsAsync(A, product));
        Assert.Equal(composed, stored);
        Assert.False(TagVocabulary.IsOverLength(stored));
        Assert.Equal(stored, TagVocabulary.FindNearDuplicate(composed, [stored])); // and after
    }

    [Fact]
    public async Task A_padded_tag_is_trimmed_and_collapsed()
    {
        // The other shrink the key makes: "a", a run of spaces, "b" is as long raw as the run, and
        // three characters once stored.
        var product = await SeedProductAsync(A, "Towels");
        await AddProductTagAsync(A, product, "  Paper" + new string(' ', TagVocabulary.MaxLength * 2) + "Goods ");

        var outcome = Boot();

        Assert.Equal(1, outcome.Rewritten);
        var (_, stored) = Assert.Single(await ProductTagsAsync(A, product));
        Assert.Equal("Paper Goods", stored);
    }

    [Fact]
    public async Task A_recipe_tag_gets_the_same_treatment()
    {
        // Both tag tables, one rule — the recipe cloud fragments exactly like the product cloud does.
        var recipe = await SeedRecipeAsync(A, "Chili");
        await AddRecipeTagAsync(A, recipe, "  Slow   Cooker ");
        await AddRecipeTagAsync(A, recipe, "Cafe\u0301");

        var outcome = Boot();

        Assert.Equal(2, outcome.Rewritten);
        Assert.Equal(["Slow Cooker", "Caf\u00E9"], await RecipeTagsAsync(A, recipe));
    }

    [Fact]
    public async Task Two_rows_that_read_the_same_once_stored_collapse_to_the_earlier_one()
    {
        // The household typed a precomposed "é"; a model later answered with "e" plus a combining accent,
        // and the pre-fold dedup let both through. Stored, they are one tag — the earlier row keeps it.
        var product = await SeedProductAsync(A, "Espresso");
        var earlier = await AddProductTagAsync(A, product, "Caf\u00E9");
        await AddProductTagAsync(A, product, "Cafe\u0301");

        var outcome = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(Rewritten: 0, Collapsed: 1, LeftAlone: 0), outcome);
        Assert.Equal([(earlier, "Caf\u00E9")], await ProductTagsAsync(A, product));
    }

    [Fact]
    public async Task A_duplicate_is_judged_the_way_the_write_path_judges_one()
    {
        // Case-insensitively, as Canonicalize asks "does the product already carry this tag" — so a
        // "snack" beside a "Snack" is a duplicate, while a "Snacks" beside a "Snack" is a NEAR-duplicate
        // and stays: that is the dedup's call at write time, with the household able to override it.
        var product = await SeedProductAsync(A, "Chips");
        var earlier = await AddProductTagAsync(A, product, "snack");
        await AddProductTagAsync(A, product, "Snack");
        var near = await AddProductTagAsync(A, product, "Snacks");

        var outcome = Boot();

        Assert.Equal(1, outcome.Collapsed);
        Assert.Equal([(earlier, "snack"), (near, "Snacks")], await ProductTagsAsync(A, product));
    }

    [Fact]
    public async Task A_clean_database_is_untouched()
    {
        var product = await SeedProductAsync(A, "Ketchup");
        foreach (var tag in TagVocabulary.Seed) await AddProductTagAsync(A, product, tag);
        var recipe = await SeedRecipeAsync(A, "Pasta");
        await AddRecipeTagAsync(A, recipe, "Dinner");

        var outcome = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(), outcome);
        Assert.Equal(TagVocabulary.Seed, (await ProductTagsAsync(A, product)).Select(t => t.Value));
        Assert.Equal(["Dinner"], await RecipeTagsAsync(A, recipe));
    }

    [Fact]
    public async Task A_second_boot_rewrites_nothing()
    {
        // It runs on EVERY boot: whatever the first pass wrote has to read as already done to the next.
        var product = await SeedProductAsync(A, "Espresso");
        await AddProductTagAsync(A, product, "Cafe\u0301");
        await AddProductTagAsync(A, product, "  Caf\u00E9 ");
        var recipe = await SeedRecipeAsync(A, "Chili");
        await AddRecipeTagAsync(A, recipe, " Slow  Cooker");

        var first = Boot();
        var second = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(Rewritten: 2, Collapsed: 1, LeftAlone: 0), first);
        Assert.Equal(new TagStoredFormMigration.Outcome(), second);
        Assert.Equal(["Caf\u00E9"], (await ProductTagsAsync(A, product)).Select(t => t.Value));
        Assert.Equal(["Slow Cooker"], await RecipeTagsAsync(A, recipe));
    }

    [Fact]
    public async Task One_households_collision_does_not_touch_anothers_identical_tag()
    {
        // A collision is judged within one household's product. B carries the same spelling that A's
        // later row loses to — rewritten into stored form like any row, and kept.
        var productA = await SeedProductAsync(A, "Espresso");
        await AddProductTagAsync(A, productA, "Caf\u00E9");
        await AddProductTagAsync(A, productA, "Cafe\u0301");
        var productB = await SeedProductAsync(B, "Espresso");
        await AddProductTagAsync(B, productB, "Cafe\u0301");

        var outcome = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(Rewritten: 1, Collapsed: 1, LeftAlone: 0), outcome);
        Assert.Equal(["Caf\u00E9"], (await ProductTagsAsync(A, productA)).Select(t => t.Value));
        Assert.Equal(["Caf\u00E9"], (await ProductTagsAsync(B, productB)).Select(t => t.Value));
    }

    [Fact]
    public async Task A_tag_too_long_in_any_form_is_left_alone_and_counted()
    {
        // Neither was ever a tag: one is past the cap in every normal form (refused before it is
        // normalized, which is the point), the other is plain text just over it. Both stay exactly as
        // written — a boot pass does not delete data on its own authority — and the count says they exist.
        var beyondAnyForm = "a" + new string('\u0301', TagVocabulary.MaxLength * TagVocabulary.MaxCanonicalExpansion);
        var justOver = new string('x', TagVocabulary.MaxLength + 1);
        var product = await SeedProductAsync(A, "Mystery");
        await AddProductTagAsync(A, product, beyondAnyForm);
        await AddProductTagAsync(A, product, justOver);

        var outcome = Boot();

        Assert.Equal(new TagStoredFormMigration.Outcome(Rewritten: 0, Collapsed: 0, LeftAlone: 2), outcome);
        Assert.Equal([beyondAnyForm, justOver], (await ProductTagsAsync(A, product)).Select(t => t.Value));
    }

    [Fact]
    public async Task A_database_without_the_recipe_tag_table_is_left_alone()
    {
        // A box that predates RecipeTags: AdditiveSchema.Apply creates the table and this runs strictly
        // after it — the guard is what stops a reordering from throwing on every boot.
        await using (var db = _db.CreateUnscopedContext())
            await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""RecipeTags"";");
        var product = await SeedProductAsync(A, "Espresso");
        await AddProductTagAsync(A, product, "Cafe\u0301");

        var outcome = Boot(); // no throw

        Assert.Equal(1, outcome.Rewritten);
    }
}
