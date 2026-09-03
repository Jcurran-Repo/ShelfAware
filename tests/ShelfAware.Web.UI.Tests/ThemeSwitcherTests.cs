using AngleSharp.Html.Dom;
using Bunit;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The header theme switcher — two independent axes: palette (Warm &amp; Fresh / Classic) and
/// light/dark (Auto/Light/Dark). The actual theming lives in wwwroot/js/theme.js (which bUnit can't
/// run), so these pin the Blazor half: it offers the right options on each axis, reads the stored
/// choices on load AND syncs both controls, and writes a picked choice back through the matching JS
/// API. The end-to-end flip (data-apptheme/data-theme, colours, persistence, no-flash) is
/// browser-verified.
/// </summary>
public class ThemeSwitcherTests : PageTestContext
{
    static IHtmlSelectElement ModeSelect(IRenderedComponent<ThemeSwitcher> cut) =>
        (IHtmlSelectElement)cut.Find("select[aria-label='Light or dark']");

    static IHtmlSelectElement PaletteSelect(IRenderedComponent<ThemeSwitcher> cut) =>
        (IHtmlSelectElement)cut.Find("select[aria-label='Colour palette']");

    [Fact]
    public void Offers_auto_light_and_dark()
    {
        var cut = Render<ThemeSwitcher>();
        var values = cut.FindAll("select[aria-label='Light or dark'] option")
            .Select(o => o.GetAttribute("value")).ToArray();
        Assert.Equal(new[] { "auto", "light", "dark" }, values);
    }

    [Fact]
    public void Offers_the_two_palettes()
    {
        var cut = Render<ThemeSwitcher>();
        var values = cut.FindAll("select[aria-label='Colour palette'] option")
            .Select(o => o.GetAttribute("value")).ToArray();
        Assert.Equal(new[] { "warm", "classic" }, values);
    }

    [Fact]
    public void Reads_both_stored_preferences_on_load()
    {
        // The switcher reflects the choices the pre-paint script already applied — it must ASK for each
        // AND sync its control (asserting the invoke alone would survive dropping the sync). Non-default
        // values on both axes so a control stuck on its default fails.
        JSInterop.Setup<string>("shelfawareTheme.get").SetResult("dark");
        JSInterop.Setup<string>("shelfawareTheme.getTheme").SetResult("classic");
        var cut = Render<ThemeSwitcher>();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("dark", ModeSelect(cut).Value);
            Assert.Equal("classic", PaletteSelect(cut).Value);
        });
    }

    [Fact]
    public void Choosing_a_mode_writes_it_through()
    {
        var cut = Render<ThemeSwitcher>();
        ModeSelect(cut).Change("light");
        cut.WaitForAssertion(() =>
        {
            var invocation = JSInterop.VerifyInvoke("shelfawareTheme.set");
            Assert.Equal("light", invocation.Arguments[0]);
        });
    }

    [Fact]
    public void Choosing_a_palette_writes_it_through()
    {
        var cut = Render<ThemeSwitcher>();
        PaletteSelect(cut).Change("classic");
        cut.WaitForAssertion(() =>
        {
            var invocation = JSInterop.VerifyInvoke("shelfawareTheme.setTheme");
            Assert.Equal("classic", invocation.Arguments[0]);
        });
    }
}
