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

    [Fact]
    public void No_step_tells_a_managed_deployment_to_bring_its_own_key()
    {
        // On a managed deployment the host supplies the keys, the browser cannot override them and the
        // Settings key panel is hidden — so "add your own key" names a control the visitor cannot see
        // and an act they cannot take. The data-independence rule's second half: a step must not assert
        // anything about the DEPLOYMENT either.
        // Phrases that can ONLY mean key custody. Deliberately not a bare "your own" — the Reports step
        // legitimately says "build your own report", and a rule that cries wolf gets loosened later by
        // someone who assumes it always does.
        string[] byokOnly = ["api key", "your browser", "your own key", "bring your own"];

        foreach (var step in TourScript.Steps)
        {
            var managedBody = step.BodyFor(managed: true);
            foreach (var phrase in byokOnly)
                Assert.DoesNotContain(phrase, managedBody, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_step_without_a_managed_variant_reads_the_same_either_way()
    {
        // Only the Settings step differs. Every other step describes a feature that works identically
        // however the deployment is keyed, and must not silently acquire a second voice.
        foreach (var step in TourScript.Steps.Where(s => s.WhenManaged is null))
        {
            Assert.Equal(step.Title, step.TitleFor(managed: true));
            Assert.Equal(step.Body, step.BodyFor(managed: true));
        }

        var settings = TourScript.Steps[^1];
        Assert.NotNull(settings.WhenManaged);
        Assert.NotEqual(settings.BodyFor(managed: false), settings.BodyFor(managed: true));
    }

    [Fact]
    public void The_byok_wording_leads_with_what_works_without_a_key()
    {
        // The old copy opened "Shelf Aware runs on your own API key", which reads as a requirement — and
        // the visitor has just been shown ten screens of a working app that needed no key at all.
        var settings = TourScript.Steps[^1];

        Assert.Contains("without an API key", settings.BodyFor(managed: false), StringComparison.OrdinalIgnoreCase);
    }

    // Each body is a two-part concatenation split for source readability; emptying ONE part leaves the
    // other, so the "not blank" check above can't see a dropped half. These pin a distinctive phrase from
    // each part — a step that loses half its copy is caught — while staying robust to ordinary rewording
    // (unlike an exact-string golden, which would break on every copy tweak and pin the emoji/curly quotes).
    [Theory]
    [InlineData(0, "running low, most urgent first")]
    [InlineData(0, "works the rhythm out from your receipts")]
    [InlineData(1, "Type it the way")]
    [InlineData(1, "keeps listening as you move around")]
    [InlineData(2, "usual brand and size")]
    [InlineData(2, "how many you have on hand")]
    [InlineData(3, "the order you walk the store")]
    [InlineData(3, "under Extras")]
    [InlineData(4, "actually on your shelves")]
    [InlineData(4, "hands-free cook-along")]
    [InlineData(5, "Photograph or upload a grocery receipt")]
    [InlineData(5, "review before anything is recorded")]
    [InlineData(6, "line by line")]
    [InlineData(6, "any receipt can be removed")]
    [InlineData(7, "cost over time")]
    [InlineData(7, "a big shop is something you see coming")]
    [InlineData(8, "build your own")]
    [InlineData(8, "chart and the table beneath it")]
    [InlineData(9, "The honest scorecard")]
    [InlineData(9, "measured, not claimed")]
    [InlineData(10, "works without an API key")]
    [InlineData(10, "receipt reading and recipe ideas")]
    [InlineData(10, "export or delete everything from this page")]
    public void Every_step_body_names_what_its_page_is_for(int step, string phrase) =>
        Assert.Contains(phrase, TourScript.Steps[step].Body, StringComparison.Ordinal);

    [Fact]
    public void The_managed_settings_step_reads_in_its_own_voice()
    {
        // On a managed deployment the last step swaps to its variant: a different title and a body about
        // exporting/deleting rather than a key. Pins that TitleFor/BodyFor actually return the variant.
        var settings = TourScript.Steps[^1];

        Assert.Equal("Your data", settings.TitleFor(managed: true));
        Assert.Contains("run on the keys whoever set this up", settings.BodyFor(managed: true), StringComparison.Ordinal);
        Assert.Contains("export everything you've got, or delete the lot", settings.BodyFor(managed: true), StringComparison.Ordinal);
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
