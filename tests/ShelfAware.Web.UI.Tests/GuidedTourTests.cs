using System.Reflection;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using ShelfAware.Core.Onboarding;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The guided walkthrough. It lives in MainLayout and navigates between its own steps, so the things
/// worth pinning are the ones a walkthrough gets wrong: advancing without moving, a dismissal that
/// doesn't stick, and ambushing a visitor who never asked for it.
/// </summary>
public class GuidedTourTests : PageTestContext
{
    private const string Load = "shelfawareTour.load";
    private const string Save = "shelfawareTour.save";
    private const string Highlight = "shelfawareTour.highlight";
    private const string SetActive = "shelfawareTour.setActive";

    /// <summary>⚠️ History, not Uri. Re-navigating to the page you are already on leaves the URI
    /// identical, so a Uri comparison cannot tell "didn't navigate" from "navigated to the same place"
    /// — and it is the navigating that does the damage, tearing the page down and rebuilding it.
    /// History counts the act.</summary>
    private BunitNavigationManager Nav => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();

    private static string Title(IRenderedComponent<GuidedTour> cut) => Collapsed(cut.Find(".tour-title"));

    private static void Click(IRenderedComponent<GuidedTour> cut, string label) =>
        cut.FindAll(".tour-actions button").Single(b => b.TextContent.Trim() == label).Click();

    /// <summary>Renders the tour and starts it through the coordinator, exactly as the dashboard banner
    /// and the Settings button do.</summary>
    private async Task<IRenderedComponent<GuidedTour>> StartedAsync()
    {
        var cut = Render<GuidedTour>();
        await cut.InvokeAsync(() => Tour.RequestStartAsync());
        return cut;
    }

    [Fact]
    public async Task Starting_shows_the_first_step_and_marks_the_walkthrough_open()
    {
        var cut = await StartedAsync();

        Assert.Equal(TourScript.Steps[0].Title, Title(cut));
        Assert.Contains("Step 1 of " + TourScript.Count, Collapsed(cut.Find(".tour-progress")));
        // The body class is what moves the voice assistant out of the way on a narrow screen.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == SetActive && Equals(i.Arguments[0], true));
    }

    [Fact]
    public async Task Next_advances_the_step_and_navigates_when_the_page_changes()
    {
        var cut = await StartedAsync();

        // Steps 1 and 2 are both the dashboard: the tour must advance WITHOUT re-navigating, or the
        // page it is describing is torn down and rebuilt underneath the panel.
        var before = Nav.History.Count;
        Click(cut, "Next");
        Assert.Equal(TourScript.Steps[1].Title, Title(cut));
        Assert.Equal(before, Nav.History.Count);

        // Step 3 is a different page, so this one does navigate.
        Click(cut, "Next");
        Assert.Equal(TourScript.Steps[2].Title, Title(cut));
        Assert.EndsWith(TourScript.Steps[2].Route, Nav.History.Last().Uri);
    }

    [Fact]
    public async Task Back_returns_to_the_previous_step_and_is_disabled_at_the_start()
    {
        var cut = await StartedAsync();
        Assert.True(cut.FindAll(".tour-actions button").Single(b => b.TextContent.Trim() == "Back").HasAttribute("disabled"));

        Click(cut, "Next");
        Click(cut, "Back");

        Assert.Equal(TourScript.Steps[0].Title, Title(cut));
    }

    [Fact]
    public async Task Each_step_asks_for_its_own_anchor_to_be_ringed()
    {
        var cut = await StartedAsync();
        Click(cut, "Next");

        var ringed = JSInterop.Invocations
            .Where(i => i.Identifier == Highlight)
            .Select(i => i.Arguments[0] as string)
            .ToList();

        Assert.Equal(TourScript.Steps[0].Anchor, ringed[0]);
        Assert.Equal(TourScript.Steps[1].Anchor, ringed[1]);
    }

    [Fact]
    public async Task The_last_step_offers_Done_instead_of_Next()
    {
        var cut = await StartedAsync();
        for (var i = 0; i < TourScript.Count - 1; i++) Click(cut, "Next");

        Assert.Equal(TourScript.Steps[^1].Title, Title(cut));
        var labels = cut.FindAll(".tour-actions button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Done", labels);
        Assert.DoesNotContain("Next", labels);
        Assert.DoesNotContain("Skip the tour", labels); // nothing left to skip
    }

    [Fact]
    public async Task Closing_hides_the_panel_and_remembers_that()
    {
        var cut = await StartedAsync();

        cut.Find(".tour-panel .icon-btn").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tour-panel")));
        // done: true is what stops the next visit resuming it; the ring and body class come off too.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == Save && Equals(i.Arguments[1], true));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == SetActive && Equals(i.Arguments[0], false));
    }

    [Fact]
    public async Task Skipping_midway_is_the_same_act_as_finishing()
    {
        var cut = await StartedAsync();
        Click(cut, "Next");
        Click(cut, "Skip the tour");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tour-panel")));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == Save && Equals(i.Arguments[1], true));
    }

    [Fact]
    public void A_half_finished_walkthrough_resumes_where_it_left_off()
    {
        JSInterop.Setup<GuidedTour.TourState?>(Load).SetResult(new GuidedTour.TourState(2, Started: true, Done: false));

        var cut = Render<GuidedTour>();

        cut.WaitForAssertion(() => Assert.Equal(TourScript.Steps[2].Title, Title(cut)));
        // Resuming must NOT navigate at all: the visitor is already looking at a page, and yanking them
        // to step three's route on a plain page load would be the tour hijacking an ordinary visit.
        Assert.Empty(Nav.History);
    }

    [Fact]
    public void A_visitor_who_never_started_one_is_left_alone()
    {
        // The default stored state. The tour is OFFERED by the dashboard banner — it does not ambush.
        JSInterop.Setup<GuidedTour.TourState?>(Load).SetResult(new GuidedTour.TourState(0, Started: false, Done: false));

        var cut = Render<GuidedTour>();

        Assert.Empty(cut.FindAll(".tour-panel"));
    }

    [Fact]
    public void A_finished_walkthrough_does_not_come_back()
    {
        JSInterop.Setup<GuidedTour.TourState?>(Load).SetResult(new GuidedTour.TourState(4, Started: true, Done: true));

        var cut = Render<GuidedTour>();

        Assert.Empty(cut.FindAll(".tour-panel"));
    }

    [Fact]
    public void A_stored_position_past_the_end_of_a_shortened_tour_resumes_at_the_last_step()
    {
        JSInterop.Setup<GuidedTour.TourState?>(Load).SetResult(new GuidedTour.TourState(99, Started: true, Done: false));

        var cut = Render<GuidedTour>();

        cut.WaitForAssertion(() => Assert.Equal(TourScript.Steps[^1].Title, Title(cut)));
    }

    [Fact]
    public void Unreadable_storage_leaves_the_layout_standing()
    {
        // Private browsing, a blocked localStorage, or the script failing to load. The walkthrough is
        // decoration over a working app: it must never be the reason a page fails to render.
        JSInterop.Setup<GuidedTour.TourState?>(Load).SetException(new JSException("localStorage is not available"));

        var cut = Render<GuidedTour>();

        Assert.Empty(cut.FindAll(".tour-panel"));
    }

    [Fact]
    public void Every_step_points_at_a_route_the_app_actually_serves()
    {
        // The anti-rot check. A renamed or retired page turns its step into a walkthrough stop that
        // navigates to a 404 — and nothing else in the suite reads the script's routes.
        var served = typeof(GuidedTour).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var step in TourScript.Steps)
            Assert.True(served.Contains(step.Route), $"The walkthrough visits {step.Route}, which no page serves.");
    }
}
