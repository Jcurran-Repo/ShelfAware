using Bunit;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The header colour-theme switcher. The actual theming lives in wwwroot/js/theme.js (which bUnit
/// can't run), so these pin the Blazor half: it offers Auto/Light/Dark, reads the stored choice on
/// load, and writes the picked one back through the JS API. The end-to-end flip (data-theme, colours,
/// persistence, no-flash) is browser-verified.
/// </summary>
public class ThemeSwitcherTests : PageTestContext
{
    [Fact]
    public void Offers_auto_light_and_dark()
    {
        var cut = Render<ThemeSwitcher>();
        var values = cut.FindAll(".theme-switch option").Select(o => o.GetAttribute("value")).ToArray();
        Assert.Equal(new[] { "auto", "light", "dark" }, values);
    }

    [Fact]
    public void Reads_the_stored_preference_on_load()
    {
        // The switcher reflects the choice the pre-paint script already applied, so it must ASK for it.
        JSInterop.Setup<string>("shelfawareTheme.get").SetResult("dark");
        var cut = Render<ThemeSwitcher>();
        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("shelfawareTheme.get"));
    }

    [Fact]
    public void Choosing_a_theme_writes_it_through()
    {
        JSInterop.Setup<string>("shelfawareTheme.get").SetResult("auto");
        var cut = Render<ThemeSwitcher>();

        cut.Find(".theme-switch select").Change("light");

        cut.WaitForAssertion(() =>
        {
            var invocation = JSInterop.VerifyInvoke("shelfawareTheme.set");
            Assert.Equal("light", invocation.Arguments[0]);
        });
    }
}
