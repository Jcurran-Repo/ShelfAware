using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Chat;
using ShelfAware.Web.Components.Layout;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The persistent voice assistant (v2.2): lives in the layout, keeps listening across ordinary
/// navigation so commands chain, stands down only on a hand-off, and speaks the same brain as
/// the text box. The loop is sequenced here by leaving captureTurn PENDING (a fresh handler with
/// no result parks the loop with the panel open) and by the STT queue, whose exhausted-queue
/// backstop of "stop listening" winds every conversation down deterministically.
/// </summary>
public class VoiceAgentTests : VoiceTestBase
{
    private Bunit.BunitJSModuleInterop? _module;

    private Bunit.BunitJSModuleInterop Module
    {
        get
        {
            if (_module is null)
            {
                _module = JSInterop.SetupModule("/js/conversation.js");
                _module.Setup<bool>("isSupported").SetResult(true);
                _module.Setup<bool>("start").SetResult(true);
            }
            return _module;
        }
    }

    private static VoiceAgent.VoiceCapture Speech => new(OneByteBase64, "audio/webm");

    private IRenderedComponent<VoiceAgent> RenderAgent()
    {
        _ = Module;
        var cut = Render<VoiceAgent>();
        cut.WaitForAssertion(() =>
            Assert.Contains(JSInterop.Invocations, i => i.Identifier == "isSupported"));
        return cut;
    }

    private static void WaitForFab(IRenderedComponent<VoiceAgent> cut) =>
        cut.WaitForState(() => cut.FindAll(".voice-agent-fab").Count == 1);

    [Fact]
    public void A_conversation_hears_answers_and_ends_on_the_spoken_stop()
    {
        Coordinator.ScreenContext = "The user is viewing the Recipes page.";
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("what am I low on?"); // then the backstop: "stop listening"
        Chat.Next = new ChatResult { Success = true, Reply = "You're low on coffee." };
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();

        // The whole exchange ran: the question reached the brain WITH the screen context (that's
        // what makes "the second one" resolvable), the reply was spoken, and the plain-code stop
        // phrase ended the session with a goodbye — without ever waking the brain for it.
        WaitForFab(cut);
        Assert.Equal(["what am I low on?"], Chat.Asked);
        Assert.Equal("The user is viewing the Recipes page.", Chat.LastScreenContext);
        Assert.Contains(Tts.Spoken, s => s.Text == "You're low on coffee.");
        Assert.Contains(Tts.Spoken, s => s.Text == "Okay — stopped listening.");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop");
    }

    [Fact]
    public void A_refused_microphone_reports_and_leaves_the_launcher_up()
    {
        Module.Setup<bool>("start").SetResult(false);
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            "Couldn't access the microphone — check the browser's mic permission.",
            cut.Find(".error").TextContent));
        Assert.NotEmpty(cut.FindAll(".voice-agent-fab"));
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public void Silence_winds_the_conversation_down_instead_of_holding_the_mic_open()
    {
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult((VoiceAgent.VoiceCapture?)null);
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();

        WaitForFab(cut); // no speech in the window → the panel folds itself away
        Assert.Empty(Stt.Heard);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop"); // the recorder was released
    }

    [Fact]
    public void An_ordinary_navigation_keeps_the_agent_listening_so_commands_chain()
    {
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("show me the grocery list"); // then the backstop stop
        Chat.Next = new ChatResult { Success = true, Reply = "Taking you there.", NavigateTo = "/list" };
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();
        WaitForFab(cut);

        // The page moved AND the loop took another turn after it (the backstop stop phrase is the
        // proof: it was heard on the destination page). This is the hands-free chain's foundation.
        Assert.EndsWith("/list", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains(Tts.Spoken, s => s.Text == "Okay — stopped listening.");
    }

    [Fact]
    public void A_non_navigating_change_pings_the_page_on_screen()
    {
        var pinged = 0;
        Coordinator.PantryChanged += () => { pinged++; return Task.CompletedTask; };
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("we're out of coffee");
        Chat.Next = new ChatResult { Success = true, Reply = "Noted." };
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();
        WaitForFab(cut);

        // The agent lives in the layout and can't reach the page's reload — the coordinator ping
        // IS the refresh path, and a data change must fire it exactly once.
        Assert.Equal(1, pinged);
    }

    [Fact]
    public void A_hand_off_stands_the_agent_down_before_the_reader_takes_the_mic()
    {
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("read me the ribs recipe");
        Chat.Next = new ChatResult
        {
            Success = true, Reply = "Starting the cook-along.", NavigateTo = "/recipes?read=7", HandsOff = true,
        };
        var cut = RenderAgent();

        cut.Find(".voice-agent-fab").Click();
        WaitForFab(cut);

        // One turn, then gone: the reader makes its own audio and opens its own mic, so the agent
        // must fully release the device first — and it must NOT say the stop goodbye (nobody
        // stopped it; it handed over).
        Assert.EndsWith("/recipes?read=7", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(["read me the ribs recipe"], Chat.Asked);
        Assert.DoesNotContain(Tts.Spoken, s => s.Text == "Okay — stopped listening.");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop");
    }

    [Fact]
    public async Task Resume_replays_the_conversation_to_the_brain_and_the_launcher_wipes_it()
    {
        // First conversation: one exchange, ended by the stop phrase.
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("what am I low on?");
        Chat.Next = new ChatResult { Success = true, Reply = "You're low on coffee." };
        var cut = RenderAgent();
        cut.Find(".voice-agent-fab").Click();
        WaitForFab(cut);

        // "Back to assistant" resumes WITHOUT wiping context. The claim that matters is not the
        // panel paint but the REPLAY: the follow-up's chat call must carry the earlier turns, or
        // "read me another one" has nothing to resolve "another" against.
        Stt.Say("read me another one");
        await Coordinator.RequestResumeAsync();
        WaitForFab(cut); // the backstop stop phrase winds it down again

        Assert.NotNull(Chat.LastHistory);
        Assert.Contains(Chat.LastHistory!, t => t.User == "what am I low on?" && t.Assistant == "You're low on coffee.");

        // The launcher is a hard reset: a brand-new conversation reaches the brain with NO history.
        Stt.Say("hello again");
        cut.Find(".voice-agent-fab").Click();
        WaitForFab(cut);
        Assert.Empty(Chat.LastHistory!);
    }

    [Fact]
    public async Task A_stand_down_request_releases_the_mic_mid_turn_but_keeps_the_history()
    {
        // Get an ACTIVE conversation suspended mid-turn: the chat call is held open on a gate, so
        // the panel is deterministically up when the stand-down lands — the exact moment a reader
        // takes the one microphone in the house.
        Module.Setup<VoiceAgent.VoiceCapture?>("captureTurn").SetResult(Speech);
        Stt.Say("what's for dinner?");
        var held = new TaskCompletionSource<ChatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Chat.Hold = held;
        var cut = RenderAgent();
        cut.Find(".voice-agent-fab").Click();
        cut.WaitForState(() => cut.FindAll(".voice-agent-panel").Count == 1);

        await Coordinator.RequestStandDownAsync();

        // The agent let go at once — recorder stopped, panel folded — without waiting for the
        // in-flight turn.
        WaitForFab(cut);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "stop");

        // The held turn completes into the kept history…
        held.SetResult(new ChatResult { Success = true, Reply = "Ribs, apparently." });

        // …and the hand-back replays it: the conversation survived the stand-down.
        Stt.Say("and dessert?");
        await Coordinator.RequestResumeAsync();
        WaitForFab(cut);
        Assert.Contains(Chat.LastHistory!, t => t.User == "what's for dinner?" && t.Assistant == "Ribs, apparently.");
    }
}
