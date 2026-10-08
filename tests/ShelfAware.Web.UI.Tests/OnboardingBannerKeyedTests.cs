using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Components;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The first-run banner on a box that already HAS a key — the managed demo, or a visitor whose own key
/// has landed. Receipts are the loop everything else hangs off, and until 2026-10-07 the only mention of
/// them sat inside the keyless branch, so the one kind of box where upload works on the first click was
/// the one that never said so (and offered an "Open Settings" button leading to "nothing to set up here").
/// </summary>
public class OnboardingBannerKeyedTests : PageTestContext
{
    protected override void RegisterAdditionalServices()
    {
        ComponentFactories.Clear();
        Services.AddSingleton(new CircuitAiSettings(
            Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "sk-host-key" })));
        Services.AddSingleton(_seeding.Seeder(Factory));
    }

    private readonly DemoSeeding _seeding = new();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _seeding.Dispose();
    }

    [Fact]
    public void A_keyed_box_points_a_new_household_at_receipts_not_at_settings()
    {
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));

        var banner = cut.Find(".onboarding");
        Assert.Contains("Start with a receipt", banner.TextContent);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Upload a receipt");
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Open Settings");
        // The BYOK pitch is for a keyless visitor only.
        Assert.DoesNotContain("your own API key", banner.TextContent);
    }

    [Fact]
    public void Upload_a_receipt_goes_to_the_upload_page()
    {
        var cut = Render<OnboardingBanner>(ps => ps.Add(p => p.CatalogEmpty, true));
        var nav = (BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload a receipt").Click();

        Assert.EndsWith("/receipt", nav.Uri);
    }
}
