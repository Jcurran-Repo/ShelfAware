using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The built-in reader (v3.3): narration STREAMS (intro plays while steps synthesize behind it,
/// appended in order), a failed step stops LOUDLY (never a silent skip — the listener's hands are
/// busy), and hands-free mode listens BETWEEN steps: the plain-code grammar first, the brain as
/// the fall-through with the recipe riding in as screen context. Segmentation must be EXACTLY
/// <see cref="RecipeNarration"/>'s — the cache keys a clip on its text AND its neighbours, and
/// the export must be able to find these clips again.
/// </summary>
public class RecipeReadAloudTests : VoiceTestBase
{
    private static Recipe Ribs => new()
    {
        Name = "Sticky Ribs",
        Blurb = "Low and slow.",
        SavedAt = DateTimeOffset.Now,
        Steps =
        [
            new RecipeStep { Order = 1, Text = "Rub the ribs." },
            new RecipeStep { Order = 2, Text = "Cook them slowly." },
        ],
    };

    private Bunit.BunitJSModuleInterop? _ear;

    /// <summary>cooklisten.js with a working microphone: supported, session opens, and every
    /// listening window hears the same one-byte utterance — the STT queue says what it MEANT.</summary>
    private void GiveWorkingEars()
    {
        _ear = JSInterop.SetupModule("/js/cooklisten.js");
        _ear.Setup<bool>("isSupported").SetResult(true);
        _ear.Setup<RecipeReadAloud.SessionResult>("startSession")
            .SetResult(new RecipeReadAloud.SessionResult(true, null, 0.01));
        _ear.Setup<RecipeReadAloud.HeardResult>("listen", _ => true)
            .SetResult(new RecipeReadAloud.HeardResult(true, OneByteBase64, "audio/webm", null));
    }

    private IRenderedComponent<RecipeReadAloud> RenderReader(bool handsFree = false,
        Action? onClosed = null)
    {
        var cut = Render<RecipeReadAloud>(ps =>
        {
            ps.Add(p => p.Recipe, Ribs);
            ps.Add(p => p.HandsFree, handsFree);
            if (onClosed is not null) ps.Add(p => p.OnClosed, onClosed);
        });
        cut.WaitForState(() => cut.FindAll(".ra-controls").Count > 0 || cut.FindAll(".error").Count > 0);
        return cut;
    }

    private List<string> PlayerCalls(params string[] identifiers) =>
        [.. JSInterop.Invocations.Where(i => identifiers.Contains(i.Identifier)).Select(i => i.Identifier)];

    // ------------------------------------------------------------------------------- narration

    [Fact]
    public void Narration_speaks_exactly_the_shared_segmentation_and_streams_in_order()
    {
        var cut = RenderReader();

        // The texts AND their neighbour contexts must be RecipeNarration's own — the cache keys a
        // clip on both, so any drift here silently orphans every cached clip the export looks for.
        var texts = RecipeNarration.Of(Ribs);
        cut.WaitForAssertion(() => Assert.Equal(texts.Count, Tts.Spoken.Count));
        for (var i = 0; i < texts.Count; i++)
        {
            Assert.Equal(texts[i].Text, Tts.Spoken[i].Text);
            Assert.Equal(RecipeNarration.ContextAt(texts, i), Tts.Spoken[i].Context);
        }

        // The intro went to the player alone and playback started BEFORE the steps landed —
        // streaming is the feature; the steps then append in step order and the stream closes.
        Assert.Equal(["load", "playFrom", "append", "append", "endOfStream"],
            PlayerCalls("load", "playFrom", "append", "endOfStream"));
        var load = JSInterop.Invocations.Single(i => i.Identifier == "load");
        Assert.True((bool)load.Arguments[2]!);  // more coming
        Assert.True((bool)load.Arguments[3]!);  // button mode runs on between steps
    }

    [Fact]
    public void A_failed_intro_names_the_likely_cause()
    {
        Tts.FailOn.Add("Sticky Ribs");
        var cut = RenderReader();

        cut.WaitForAssertion(() => Assert.Contains(
            "Couldn't generate the narration — check that the ElevenLabs key is set.",
            cut.Find(".error").TextContent));
        Assert.Empty(cut.FindAll(".ra-controls")); // nothing to control
    }

    [Fact]
    public void A_failed_step_stops_loudly_instead_of_skipping_it()
    {
        // Step 2 won't synthesize. Skipping it silently would lose a COOKING step on someone whose
        // hands are busy — the reader must stop there and say so; the steps stay on screen.
        Tts.FailOn.Add("Cook them slowly");
        var cut = RenderReader();

        cut.WaitForAssertion(() => Assert.Contains(
            "Couldn't narrate step 2. The remaining steps are listed above.",
            cut.Find(".error").TextContent));
        // Step 1 made it into the playlist; the stream was then closed so the player can finish.
        Assert.Equal(["append", "endOfStream"], PlayerCalls("append", "endOfStream"));
    }

    [Fact]
    public async Task The_buttons_drive_the_player_and_the_highlight_follows_it()
    {
        var cut = RenderReader();
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        cut.FindAll(".ra-controls button").Single(b => b.TextContent.Contains("Next")).Click();
        cut.FindAll(".ra-controls button").Single(b => b.TextContent.Contains("Prev")).Click();
        cut.FindAll(".ra-controls button").Single(b => b.TextContent.Contains("Pause")).Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "next");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "prev");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "togglePause");

        // The player reports where it IS; the highlight follows the report, never guesses.
        await cut.Instance.OnIndex(2);
        var current = Assert.Single(cut.FindAll(".ra-steps li.current"));
        Assert.Contains("Cook them slowly.", current.TextContent);
        Assert.Contains("Step 2 of 2", cut.Markup);

        await cut.Instance.OnFinished();
        cut.FindAll(".ra-controls button").Single(b => b.TextContent.Contains("Replay")).Click();
        Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier == "playFrom")); // start + replay
    }

    [Fact]
    public void Close_stops_the_player_and_back_to_assistant_also_resumes_the_agent()
    {
        var closed = 0;
        var resumed = 0;
        Coordinator.ResumeRequested += () => { resumed++; return Task.CompletedTask; };
        var cut = RenderReader(onClosed: () => closed++);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        cut.Find("button[aria-label='Stop reading Sticky Ribs']").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, closed));
        Assert.Equal(0, resumed); // ✕ is just "stop" — no hand-back

        cut.Find("button[aria-label='Stop reading and hand back to the voice assistant']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, closed));
        Assert.Equal(1, resumed); // narration stopped FIRST, then the agent got the mic
    }

    // ------------------------------------------------------------------------------ hands-free

    [Fact]
    public async Task Hands_free_takes_the_mic_from_the_agent_and_the_grammar_moves_the_reader_for_free()
    {
        var stoodDown = 0;
        Coordinator.StandDownRequested += () => { stoodDown++; return Task.CompletedTask; };
        GiveWorkingEars();
        Stt.Say("next");
        var cut = RenderReader(handsFree: true);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        // One microphone: the roaming agent was asked to let go before this reader opened ears.
        Assert.Equal(1, stoodDown);
        // Hands-free stops after each step for the cook's turn — autoAdvance off.
        Assert.False((bool)JSInterop.Invocations.Single(i => i.Identifier == "load").Arguments[3]!);

        // A step ends; the cook says "next". The plain-code grammar owns it: the player moves and
        // the brain was never woken — instant, free, deterministic.
        await cut.Instance.OnStepFinished(1);

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "next");
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public void A_browser_without_ears_keeps_the_reader_on_buttons()
    {
        JSInterop.SetupModule("/js/cooklisten.js").Setup<bool>("isSupported").SetResult(false);
        var cut = RenderReader(handsFree: true);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        // A refusal is not a failure: the recipe still reads, the buttons still work — the mic
        // was always an addition, never a requirement.
        cut.WaitForAssertion(() =>
            Assert.Contains("This browser can't listen. Use the buttons above.", cut.Find(".ra-listen").TextContent));
        Assert.NotEmpty(cut.FindAll(".ra-controls button"));
        Assert.Contains("Try again", cut.Find(".ra-listen button").TextContent);
    }

    [Fact]
    public void A_denied_microphone_says_so_plainly()
    {
        _ear = JSInterop.SetupModule("/js/cooklisten.js");
        _ear.Setup<bool>("isSupported").SetResult(true);
        _ear.Setup<RecipeReadAloud.SessionResult>("startSession")
            .SetResult(new RecipeReadAloud.SessionResult(false, "NotAllowedError", 0));
        var cut = RenderReader(handsFree: true);

        cut.WaitForAssertion(() =>
            Assert.Contains("No microphone access. Use the buttons above.", cut.Find(".ra-listen").TextContent));
    }

    [Fact]
    public async Task A_question_reaches_the_brain_with_the_recipe_as_context_and_the_answer_is_spoken()
    {
        GiveWorkingEars();
        Stt.Say("can I use butter instead of oil?", "stop listening");
        Chat.Next = new ChatResult { Success = true, Reply = "Butter works — watch the heat." };
        var cut = RenderReader(handsFree: true);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        await cut.Instance.OnStepFinished(1);

        // The fall-through IS the cook-along: the same brain, with the recipe and the "they're
        // listening, not reading" instruction riding in as screen context.
        Assert.Equal(["can I use butter instead of oil?"], Chat.Asked);
        Assert.Contains("Sticky Ribs", Chat.LastScreenContext);
        Assert.Contains("hands-free", Chat.LastScreenContext);
        Assert.Contains("(2) Cook them slowly.", Chat.LastScreenContext);
        Assert.Contains("go_to_step", Chat.LastScreenContext);
        // The answer went out loud, and the turn continued listening — the follow-up "stop
        // listening" then put the mic down but kept the recipe.
        Assert.Contains(Tts.Spoken, s => s.Text == "Butter works — watch the heat.");
        Assert.Contains("Voice off — the recipe's still here.", cut.Find(".ra-listen").TextContent);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "endSession");
        Assert.NotEmpty(cut.FindAll(".ra-controls button")); // buttons survive the mic going down
    }

    [Fact]
    public async Task The_brain_can_move_the_reader_and_the_step_is_the_answer()
    {
        GiveWorkingEars();
        Stt.Say("could we perhaps revisit the beginning bit");
        Chat.Next = new ChatResult { Success = true, Reply = "Taking you to step one.", StepTarget = 1 };
        var cut = RenderReader(handsFree: true);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        await cut.Instance.OnStepFinished(2);

        // go_to_step is the safety net under the conservative grammar: the model understood the
        // move, so the reader MOVES — reading "sure, here's step one" first would only be latency.
        Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier == "playFrom")); // start + the jump
        Assert.DoesNotContain(Tts.Spoken, s => s.Text == "Taking you to step one.");
    }

    [Fact]
    public async Task Hold_on_ignores_the_room_and_a_command_releases_it()
    {
        GiveWorkingEars();
        // "hold on" → held; kitchen chatter → ignored (the brain must NOT wake); "next" → moves on.
        Stt.Say("hold on", "the oven timer is beeping again", "next");
        var cut = RenderReader(handsFree: true);
        cut.WaitForState(() => Tts.Spoken.Count == 3);

        await cut.Instance.OnStepFinished(1);

        Assert.Empty(Chat.Asked); // held means held — a rustling bag can't trigger a model call
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "next");
    }
}
