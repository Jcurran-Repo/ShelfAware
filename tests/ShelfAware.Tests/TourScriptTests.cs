using ShelfAware.Core.Onboarding;

namespace ShelfAware.Tests;

/// <summary>
/// The guided walkthrough's step list and the rules for moving through it. The position is read back
/// from the visitor's own browser, so every boundary here is reachable by ordinary use (a shorter tour
/// shipping after someone left off on step eleven) as well as by a hand-edited storage value.
/// </summary>
public class TourScriptTests
{
    [Fact]
    public void Every_step_says_something()
    {
        Assert.NotEmpty(TourScript.Steps);

        foreach (var step in TourScript.Steps)
        {
            Assert.StartsWith("/", step.Route);
            Assert.False(string.IsNullOrWhiteSpace(step.Title), $"{step.Route} has no title");
            Assert.False(string.IsNullOrWhiteSpace(step.Body), $"{step.Route} has no body");
            // An anchor is optional, but an empty string isn't "no anchor" — it's a selector that
            // matches nothing, silently, forever. Say null and mean it.
            Assert.True(step.Anchor is null or { Length: > 0 }, $"{step.Route} has a blank anchor");
        }
    }

    [Fact]
    public void The_walkthrough_covers_every_surface_a_new_household_has_to_find()
    {
        // The tour is the only thing that introduces these pages; the nav bar names them but says
        // nothing about what they're for. Dropping one from the script is a silent regression in
        // discoverability, which nothing else in the suite would notice.
        string[] expected = ["/", "/products", "/list", "/recipes", "/receipt", "/receipts", "/trends", "/reports", "/accuracy", "/settings"];

        Assert.Equal(expected.Order(), TourScript.Steps.Select(s => s.Route).Distinct().Order());
    }

    [Fact]
    public void Consecutive_steps_never_repeat_a_title()
    {
        // Two steps share the dashboard by design. Identical headings on both would read as the tour
        // having failed to advance — the panel's step counter is the only other thing that moves.
        var titles = TourScript.Steps.Select(s => s.Title).ToList();
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void A_position_below_the_start_resolves_to_the_first_step(int stored, int expected) =>
        Assert.Equal(expected, TourScript.ClampIndex(stored));

    [Fact]
    public void A_position_past_the_end_resolves_to_the_last_step()
    {
        // The case a shortened tour creates for anyone who was mid-way through the longer one.
        Assert.Equal(TourScript.Count - 1, TourScript.ClampIndex(TourScript.Count));
        Assert.Equal(TourScript.Count - 1, TourScript.ClampIndex(999));
        Assert.Equal(TourScript.Steps[^1], TourScript.At(999));
    }

    [Fact]
    public void First_and_last_are_reported_on_the_clamped_position_not_the_raw_one()
    {
        // Back/Next are enabled off these, so an out-of-range stored position must not offer a
        // "Next" that leads nowhere or a "Back" that does nothing.
        Assert.True(TourScript.IsFirst(-3));
        Assert.True(TourScript.IsLast(999));
        Assert.False(TourScript.IsLast(0));
        Assert.False(TourScript.IsFirst(1));
    }

    [Fact]
    public void No_step_asserts_a_fact_about_the_household_s_own_data()
    {
        // The tour is offered to a real new household as well as to a demo visitor, so copy naming a
        // seeded row would be a screen stating something the engine never produced. Guards the names
        // most likely to be reached for when writing a step against the sample catalog.
        string[] seededHeroes = ["Beef Chuck Roast", "Canned Black Beans", "Quarter Cow", "Drink Mix", "Ground Chuck", "White Rice"];

        foreach (var step in TourScript.Steps)
        foreach (var hero in seededHeroes)
            Assert.DoesNotContain(hero, step.Body, StringComparison.OrdinalIgnoreCase);
    }
}
