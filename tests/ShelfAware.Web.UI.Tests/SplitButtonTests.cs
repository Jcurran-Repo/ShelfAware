using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The split button: one prominent primary action, alternates behind a caret. The menu's open/
/// close discipline is the behavior — alternates must be reachable but never a stray click away
/// from firing, and every close path (selection, backdrop, Escape) must actually close.
/// </summary>
public class SplitButtonTests : PageTestContext
{
    private int primaryClicks;

    private IRenderedComponent<SplitButton> RenderButton(bool disabled = false) =>
        Render<SplitButton>(ps => ps
            .Add(p => p.PrimaryLabel, "Bought today")
            .Add(p => p.OnPrimary, () => primaryClicks++)
            .Add(p => p.Disabled, disabled)
            .AddChildContent($"<button onclick=\"\" class=\"alt-action\">Restocked</button>"));

    [Fact]
    public void The_menu_stays_closed_until_the_caret_asks_for_it()
    {
        var cut = RenderButton();

        Assert.Empty(cut.FindAll(".split-menu"));
        Assert.Equal("false", cut.Find(".split-caret").GetAttribute("aria-expanded"));

        cut.Find(".split-caret").Click();

        Assert.Single(cut.FindAll(".split-menu"));
        Assert.Equal("true", cut.Find(".split-caret").GetAttribute("aria-expanded"));
        Assert.Contains("Restocked", cut.Find(".split-menu").TextContent);
    }

    [Fact]
    public void Choosing_an_alternate_closes_the_menu()
    {
        var cut = RenderButton();
        cut.Find(".split-caret").Click();

        // The click bubbles to the menu's own close handler — selection is a one-shot.
        cut.Find(".split-menu .alt-action").Click();

        Assert.Empty(cut.FindAll(".split-menu"));
    }

    [Fact]
    public void The_backdrop_and_Escape_both_close_without_firing_anything()
    {
        var cut = RenderButton();

        cut.Find(".split-caret").Click();
        cut.Find(".split-backdrop").Click();
        Assert.Empty(cut.FindAll(".split-menu"));

        cut.Find(".split-caret").Click();
        cut.Find(".split-caret").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll(".split-menu"));

        Assert.Equal(0, primaryClicks); // dismissing is not choosing
    }

    [Fact]
    public void The_primary_fires_its_action_and_closes_any_open_menu()
    {
        var cut = RenderButton();
        cut.Find(".split-caret").Click();

        cut.Find(".split-main").Click();

        Assert.Equal(1, primaryClicks);
        Assert.Empty(cut.FindAll(".split-menu"));
    }

    [Fact]
    public void Disabled_reaches_both_the_primary_and_the_caret()
    {
        // Half-disabled would leave the menu reachable while the primary is in flight — the flag
        // exists to freeze the whole control during a save.
        var cut = RenderButton(disabled: true);

        Assert.True(cut.Find(".split-main").HasAttribute("disabled"));
        Assert.True(cut.Find(".split-caret").HasAttribute("disabled"));
    }
}
