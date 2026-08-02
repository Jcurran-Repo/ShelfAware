using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The first-run banner: a keyless visitor gets the BYOK pitch, an empty catalog always offers
/// the one-click sample pantry (dismissal notwithstanding — a brand-new visitor must always have
/// a way in), and "Load sample data" runs the REAL demo seeder so the button's promise is the
/// seeded catalog, not a fake's nod.
/// </summary>
public class OnboardingBannerTests : PageTestContext
{
    protected override void RegisterAdditionalServices()
    {
        // These tests render the banner ITSELF — clear the page-harness stubs (the pages stub it
        // as an AI-adjacent child; here it is the subject).
        ComponentFactories.Clear();
        Services.AddSingleton(new CircuitAiSettings(Options.Create(new LlmOptions()))); // keyless
        Services.AddSingleton(_seeding.Seeder(Factory));
    }

    // Seeding writes the sample pending receipt's image through real storage, so it needs somewhere on
    // disk to put it. Field-initializer state only — RegisterAdditionalServices runs from the base ctor,
    // before this class's own initializers would have run if it were assigned any later.
    private readonly DemoSeeding _seeding = new();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _seeding.Dispose();
    }

    [Fact]
    public void A_keyless_visitor_with_an_empty_catalog_gets_the_full_pitch()
    {
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));

        var banner = cut.Find(".onboarding");
        Assert.Contains("your own API key", banner.TextContent);
        Assert.Contains("Load sample data", banner.TextContent);
    }

    [Fact]
    public async Task Load_sample_seeds_the_real_catalog_and_tells_the_host_page()
    {
        var seeded = 0;
        var cut = Render<OnboardingBanner>(ps => ps
            .Add(p => p.CatalogEmpty, true)
            .Add(p => p.OnSeeded, () => seeded++));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Load sample data").Click();

        cut.WaitForAssertion(() => Assert.NotEqual("", cut.Find("[role=status]").TextContent.Trim()));
        Assert.Equal(1, seeded); // the dashboard reloads off this callback — a silent seed is a ghost town

        await using var raw = Db.CreateUnscopedContext();
        Assert.True(await raw.Products.IgnoreQueryFilters().AnyAsync()); // the REAL seeder ran
    }

    [Fact]
    public async Task Loading_sample_data_rolls_straight_into_the_walkthrough()
    {
        // A pantry suddenly full of unfamiliar sample data is the moment the tour is worth most —
        // it finally has something to point at on every page it visits.
        var started = 0;
        Tour.StartRequested += () => { started++; return Task.CompletedTask; };
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Load sample data").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, started));
        await using var raw = Db.CreateUnscopedContext();
        Assert.True(await raw.Products.IgnoreQueryFilters().AnyAsync()); // started AFTER the seed landed
    }

    [Fact]
    public void A_second_load_that_seeds_nothing_does_not_start_the_walkthrough()
    {
        // The seeder is guarded on an empty pantry. A no-op run must not launch a tour of a catalog
        // this household already knows — the banner would otherwise re-announce itself on every click.
        var started = 0;
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Load sample data").Click();
        cut.WaitForAssertion(() => Assert.NotEqual("", cut.Find("[role=status]").TextContent.Trim()));

        Tour.StartRequested += () => { started++; return Task.CompletedTask; };
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Load sample data").Click();

        // Wait on the GUARD's own message, not merely on "some message" — the first click already left
        // one on screen, so anything weaker would pass without the second click having done a thing.
        cut.WaitForAssertion(() =>
            Assert.Contains("already has items", cut.Find("[role=status]").TextContent));
        Assert.Equal(0, started);
    }

    [Fact]
    public void Take_the_tour_is_offered_without_seeding_anything()
    {
        // A real new household with its own receipts gets the walkthrough too; it just isn't automatic.
        var started = 0;
        Tour.StartRequested += () => { started++; return Task.CompletedTask; };
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, false));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Take the tour").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, started));
    }

    [Fact]
    public void Dismiss_hides_the_pitch_but_an_empty_catalog_keeps_the_banner()
    {
        // Dismissal is for the key nag; the sample-data offer survives it — the one path into the
        // app for a visitor who dismissed the banner on sight and then found an empty pantry.
        var emptyCatalog = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));
        emptyCatalog.Find(".onboarding-x").Click();
        Assert.NotEmpty(emptyCatalog.FindAll(".onboarding"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "shelfawareOnboarding.dismiss");
    }

    [Fact]
    public void Dismissing_with_a_stocked_catalog_hides_the_banner_entirely()
    {
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, false));
        Assert.NotEmpty(cut.FindAll(".onboarding"));

        cut.Find(".onboarding-x").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".onboarding")));
    }

    [Fact]
    public void A_previously_dismissed_visitor_is_not_nagged_again()
    {
        JSInterop.Setup<bool>("shelfawareOnboarding.isDismissed").SetResult(true);

        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, false));

        // The dismissal flag loads after first render — the banner then stands down.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".onboarding")));
    }
}
