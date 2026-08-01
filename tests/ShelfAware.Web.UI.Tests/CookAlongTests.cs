using ShelfAware.Core.Domain;
using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The ElevenLabs realtime cook-along: the one surface that hands the LOOP to the vendor's agent
/// on purpose (native barge-in). What's ours to pin: the recipe text the agent's dynamic variable
/// is filled with, the status/mode/transcript callbacks, the fall-back contract (OnUnavailable
/// fires ONCE, so the parent can swap in our own reader without a dead-end), and both hand-back
/// roads ending the session before the assistant resumes.
/// </summary>
public class CookAlongTests : VoiceTestBase
{
    private static Recipe Ribs => new()
    {
        Name = "Sticky Ribs",
        Blurb = "Low and slow.",
        SavedAt = DateTimeOffset.Now,
        EstimatedCaloriesPerServing = 450,
        Steps =
        [
            new RecipeStep { Order = 2, Text = "Cook them slowly." },
            new RecipeStep { Order = 1, Text = "Rub the ribs." },
        ],
    };

    private IRenderedComponent<CookAlong> RenderCookAlong(
        Action? onClosed = null, Action? onUnavailable = null)
    {
        JSInterop.SetupModule("/js/cookalong.js").Setup<bool>("start", _ => true).SetResult(true);
        var cut = Render<CookAlong>(ps =>
        {
            ps.Add(p => p.Recipe, Ribs);
            if (onClosed is not null) ps.Add(p => p.OnClosed, onClosed);
            if (onUnavailable is not null) ps.Add(p => p.OnUnavailable, onUnavailable);
        });
        cut.WaitForAssertion(() =>
            Assert.Contains(JSInterop.Invocations, i => i.Identifier == "start"));
        return cut;
    }

    [Fact]
    public void The_agent_receives_the_recipe_with_ordered_steps_and_the_calorie_estimate()
    {
        var cut = RenderCookAlong();

        var recipeText = (string)JSInterop.Invocations.Single(i => i.Identifier == "start").Arguments[0]!;
        Assert.StartsWith("Sticky Ribs. Low and slow.", recipeText);
        Assert.Contains("Estimated calories per serving: ~450", recipeText);
        // Steps travel in ORDER order, renumbered — the agent reads them, so a scrambled list
        // would be read scrambled.
        Assert.Contains("1. Rub the ribs.\n2. Cook them slowly.", recipeText);
        Assert.Contains("Connecting…", cut.Find(".cookalong-status").TextContent);
    }

    [Fact]
    public async Task Mode_and_transcript_callbacks_drive_the_panel()
    {
        var cut = RenderCookAlong();

        await cut.Instance.OnMode("speaking");
        Assert.Contains("Chef is speaking…", cut.Find(".cookalong-status").TextContent);

        await cut.Instance.OnMode("listening");
        Assert.Contains("Listening — go ahead", cut.Find(".cookalong-status").TextContent);

        await cut.Instance.OnTranscript("user", "next");
        await cut.Instance.OnTranscript("agent", "Step two: cook them slowly.");
        var log = cut.Find(".convo-log");
        Assert.Contains("next", log.QuerySelector(".convo-user")!.TextContent);
        Assert.Contains("Step two", log.QuerySelector(".convo-bot")!.TextContent);
    }

    [Fact]
    public async Task A_session_that_cannot_start_reports_and_offers_the_fallback_exactly_once()
    {
        var unavailable = 0;
        var cut = RenderCookAlong(onUnavailable: () => unavailable++);

        await cut.Instance.OnStatus("unconfigured");
        Assert.Contains("isn't set up on the server", cut.Find(".cookalong-status").TextContent);
        Assert.Equal(1, unavailable);

        // A second failure signal must not re-trigger the fallback — the parent may already have
        // swapped this component for the built-in reader, and a second swap would fight it.
        await cut.Instance.OnStatus("error");
        Assert.Equal(1, unavailable);
    }

    [Fact]
    public void The_close_button_ends_the_session_without_resuming_the_assistant()
    {
        var closed = 0;
        var resumed = 0;
        Coordinator.ResumeRequested += () => { resumed++; return Task.CompletedTask; };
        var cut = RenderCookAlong(onClosed: () => closed++);

        cut.Find("button[aria-label='End cook-along']").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, closed));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop");
        Assert.Equal(0, resumed); // ✕ is "I'm done", not "hand me back"
    }

    [Fact]
    public async Task Both_hand_back_roads_stop_the_session_first_then_resume_the_assistant()
    {
        var closed = 0;
        var resumed = 0;
        Coordinator.ResumeRequested += () => { resumed++; return Task.CompletedTask; };
        var cut = RenderCookAlong(onClosed: () => closed++);

        cut.Find("button[aria-label='End cook-along and hand back to the voice assistant']").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, resumed));
        Assert.Equal(1, closed);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop"); // its audio stops FIRST

        // The spoken "go to the assistant" lands on the same road as the button.
        await cut.Instance.OnHandOff();
        Assert.Equal(2, resumed);
        Assert.Equal(2, closed);
    }
}
