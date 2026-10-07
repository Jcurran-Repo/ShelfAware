using ShelfAware.Core;

namespace ShelfAware.Tests;

/// <summary>The one input cap for the three prose boxes that feed a charged prompt (<see cref="PromptInput"/>):
/// judged trimmed, refused one over, and the refusal names the cap it was judged against.</summary>
public class PromptInputTests
{
    [Theory]
    [InlineData(PromptInput.ChatMaxLength)]
    [InlineData(PromptInput.RecipeRequestMaxLength)]
    [InlineData(PromptInput.PastedRecipeMaxLength)]
    public void Exactly_the_cap_is_allowed_and_one_more_is_not(int max)
    {
        Assert.False(PromptInput.IsOverLength(new string('x', max), max));
        Assert.True(PromptInput.IsOverLength(new string('x', max + 1), max));
    }

    [Fact]
    public void Surrounding_whitespace_never_tips_a_legitimate_input_over()
    {
        var atCap = new string('x', PromptInput.ChatMaxLength);
        Assert.False(PromptInput.IsOverLength("  " + atCap + "\n\n", PromptInput.ChatMaxLength));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_over_length(string? text) =>
        Assert.False(PromptInput.IsOverLength(text, PromptInput.ChatMaxLength));

    [Fact]
    public void The_message_quotes_the_cap_it_judged_against()
    {
        Assert.Contains("500", PromptInput.TooLongMessage(PromptInput.ChatMaxLength));
        // Thousands-separated, so the pasted-recipe cap reads as a number rather than a typo.
        Assert.Contains("20,000", PromptInput.TooLongMessage(PromptInput.PastedRecipeMaxLength));
    }

    [Fact]
    public void The_caps_are_ordered_by_what_each_box_is_for()
    {
        // A mood for dinner is shorter than a dozen pantry updates, which is far shorter than a recipe page.
        Assert.True(PromptInput.RecipeRequestMaxLength < PromptInput.ChatMaxLength);
        Assert.True(PromptInput.ChatMaxLength < PromptInput.PastedRecipeMaxLength);
    }
}
