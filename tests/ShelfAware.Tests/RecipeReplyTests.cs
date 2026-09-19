using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

/// <summary>
/// ⚠️ Direct cover for the predicate that decides whether a recipe act is paid for or given back.
/// <c>RecipeReply.Landed</c> was added as THE one definition of "a reply the household can be given",
/// consolidating two hand-kept copies — and shipped with no test in any suite, in the only project the
/// mutation gate measures. The one definition a whole class of billing decisions now rests on was the
/// least-covered code in it.
/// <para>⚠️ Both halves are load-bearing and they fail differently. "Present" alone was what
/// <c>AnthropicRecipeAdvisor.SuggestAsync</c> checked for one commit while this predicate meant present
/// AND NAMED, so a reply of a single nameless variant was refunded by the adapt path and charged in full
/// by the suggest path, eight lines apart in one file. <c>RecipeJson.Parse</c> keeps an unnamed entry —
/// <c>name</c> falls back to "" — so the nameless case is reachable, not theoretical.</para>
/// </summary>
public class RecipeReplyTests
{
    private static RecipeSuggestion Named(string name) => new(name, "", [], [], null, null);

    [Fact]
    public void A_reply_that_never_arrived_did_not_land() =>
        Assert.False(((RecipeSuggestion?)null).Landed());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void A_variant_with_no_name_did_not_land(string name) =>
        Assert.False(Named(name).Landed());

    [Fact]
    public void A_named_variant_landed() =>
        Assert.True(Named("Chickpea Tacos").Landed());

    /// <summary>⚠️ A named variant lands even with nothing else in it. The screen can render a name and
    /// say the ingredients are missing; it cannot render anything at all without one. Pinned so the
    /// predicate is not quietly widened into "a reply that is any good", which is a different question
    /// and belongs to whoever is showing it.</summary>
    [Fact]
    public void A_named_variant_with_no_ingredients_or_steps_still_landed() =>
        Assert.True(new RecipeSuggestion("Toast", "", [], [], null, null).Landed());
}
