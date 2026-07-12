using System.Text.Json;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

/// <summary>
/// The Recipes page persists the last "Get ideas" batch as JSON (SettingKeys.LastRecipeSuggestions).
/// These tests pin the storage contract: every generated field round-trips, and the DERIVED members
/// (Have, ToGrab) stay out of the payload — they must recompute live against the current pantry, never
/// replay a stale verdict from the day the batch was generated.
/// </summary>
public class RecipeSuggestionStorageTests
{
    private static RecipeSuggestion Sample() => new(
        "Skillet Chicken & Rice",
        "A fast one-pan dinner.",
        [
            new SuggestedIngredient("chicken breast", IsMain: true, MatchedProduct: "Chicken Breast", Quantity: "1 lb"),
            new SuggestedIngredient("soy sauce", IsMain: false, MatchedProduct: null),
        ],
        ["Sear the chicken.", "Add rice and simmer."],
        540);

    [Fact]
    public void A_suggestion_round_trips_through_json_intact()
    {
        var restored = JsonSerializer.Deserialize<RecipeSuggestion>(JsonSerializer.Serialize(Sample()))!;

        Assert.Equal("Skillet Chicken & Rice", restored.Name);
        Assert.Equal("A fast one-pan dinner.", restored.Blurb);
        Assert.Equal(540, restored.CaloriesPerServing);
        Assert.Equal(["Sear the chicken.", "Add rice and simmer."], restored.Steps);
        Assert.Collection(restored.Ingredients,
            i =>
            {
                Assert.Equal(("chicken breast", true, "Chicken Breast", "1 lb"), (i.Name, i.IsMain, i.MatchedProduct, i.Quantity));
                Assert.True(i.Have); // derived correctly after the trip
            },
            i => Assert.Equal(("soy sauce", false, null, null), (i.Name, i.IsMain, i.MatchedProduct, i.Quantity)));
    }

    [Fact]
    public void Derived_members_are_not_persisted()
    {
        var json = JsonSerializer.Serialize(Sample());

        Assert.DoesNotContain("ToGrab", json);
        Assert.DoesNotContain("Have", json);
    }
}
