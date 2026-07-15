using ShelfAware.Core.Domain;
using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// How a recipe becomes the things said out loud. This is a CONTRACT, not a formatting preference: the
/// speech cache keys every clip on its text and its neighbours, so the export can only find a clip the
/// reader stored by segmenting the recipe identically. Change these strings and yesterday's audio becomes
/// unreachable — which is why the rule has one home and this pins it.
/// </summary>
public class RecipeNarrationTests
{
    private static Recipe Chicken(string? blurb = null) => new()
    {
        Name = "Chicken Thighs",
        Blurb = blurb,
        Steps =
        {
            new RecipeStep { Order = 2, Text = "Roast for 40 minutes." },
            new RecipeStep { Order = 1, Text = "Heat the oven." },
        },
    };

    [Fact]
    public void The_intro_is_the_name_and_blurb_then_the_steps_in_order()
    {
        var segments = RecipeNarration.Of(Chicken("Weeknight easy."));

        Assert.Equal(["intro", "step-1", "step-2"], segments.Select(s => s.Name));
        Assert.Equal("Chicken Thighs. Weeknight easy.", segments[0].Text);
        Assert.Equal("Step 1. Heat the oven.", segments[1].Text);   // Order, not list position
        Assert.Equal("Step 2. Roast for 40 minutes.", segments[2].Text);
    }

    [Fact]
    public void A_recipe_with_no_blurb_still_introduces_itself()
    {
        Assert.Equal("Chicken Thighs.", RecipeNarration.Of(Chicken()).First().Text);
    }

    [Fact]
    public void A_recipe_with_no_steps_is_just_its_intro()
    {
        var segments = RecipeNarration.Of(new Recipe { Name = "Toast" });

        Assert.Equal("Toast.", Assert.Single(segments).Text);
    }

    [Fact]
    public void Context_is_the_neighbouring_segments_and_nothing_beyond_the_ends()
    {
        var segments = RecipeNarration.Of(Chicken("Weeknight easy."));

        var first = RecipeNarration.ContextAt(segments, 0);
        Assert.Null(first.Previous);
        Assert.Equal("Step 1. Heat the oven.", first.Next);

        var middle = RecipeNarration.ContextAt(segments, 1);
        Assert.Equal("Chicken Thighs. Weeknight easy.", middle.Previous);
        Assert.Equal("Step 2. Roast for 40 minutes.", middle.Next);

        var last = RecipeNarration.ContextAt(segments, 2);
        Assert.Equal("Step 1. Heat the oven.", last.Previous);
        Assert.Null(last.Next);
    }
}
