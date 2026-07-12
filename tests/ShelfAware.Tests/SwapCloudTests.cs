using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

public class SwapCloudTests
{
    private static PantryProduct P(string name, params string[] alsoWorksAs) => new(name, alsoWorksAs);

    [Fact]
    public void A_product_whose_name_contains_the_ingredient_is_a_stand_in()
    {
        var cloud = SwapCloud.CuratedStandIns("chicken breast", [P("Chicken Breast Tenderloins")]);
        Assert.Equal(new[] { "Chicken Breast Tenderloins" }, cloud);
    }

    [Fact]
    public void A_product_that_lists_the_ingredient_as_a_substitute_is_a_stand_in()
    {
        var cloud = SwapCloud.CuratedStandIns("chicken breast", [P("Chicken Thighs", "chicken breast")]);
        Assert.Equal(new[] { "Chicken Thighs" }, cloud);
    }

    [Fact]
    public void A_product_that_is_the_ingredient_itself_is_not_offered_as_a_swap()
    {
        // Swapping "chicken breast" to a product named "Chicken Breasts" would just remake the recipe.
        Assert.Empty(SwapCloud.CuratedStandIns("chicken breast", [P("Chicken Breasts")]));
    }

    [Fact]
    public void An_unrelated_product_is_not_a_stand_in()
    {
        Assert.Empty(SwapCloud.CuratedStandIns("chicken breast", [P("Chicken Broth"), P("Lean Ground Beef")]));
    }

    [Fact]
    public void Merge_puts_curated_products_first_and_drops_generated_forms_they_already_represent()
    {
        var merged = SwapCloud.Merge(
            curated: ["Chicken Breast Tenderloins"],
            generated: ["chicken tenderloins", "chicken thighs"]);
        // The generic "chicken tenderloins" is subsumed by the concrete product; thighs survive.
        Assert.Equal(new[] { "Chicken Breast Tenderloins", "chicken thighs" }, merged);
    }

    [Fact]
    public void Merge_with_no_curated_stand_ins_returns_the_generated_forms_unchanged()
    {
        Assert.Equal(new[] { "chicken thighs" }, SwapCloud.Merge([], ["chicken thighs"]));
    }
}
