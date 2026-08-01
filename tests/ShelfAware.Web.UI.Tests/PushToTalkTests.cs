using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Chat;
using ShelfAware.Web.Components;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Push-to-talk: a stateless one-shot around the SAME chat brain as the text box — hold, speak,
/// release; STT → chat → TTS read-back. The state machine is the behavior: what each failure
/// point says, what it never does (no chat call for silence), and the keyboard hold's
/// auto-repeat guard.
/// </summary>
public class PushToTalkTests : VoiceTestBase
{
    private Bunit.BunitJSModuleInterop? _voice;

    /// <summary>The voice.js module double — created on first touch with isSupported=true, so a
    /// test can script the capture before or after rendering.</summary>
    private Bunit.BunitJSModuleInterop Voice
    {
        get
        {
            if (_voice is null)
            {
                _voice = JSInterop.SetupModule("/js/voice.js");
                _voice.Setup<bool>("isSupported").SetResult(true);
            }
            return _voice;
        }
    }

    private IRenderedComponent<PushToTalk> RenderMic(bool supported = true)
    {
        if (supported) _ = Voice;
        else JSInterop.SetupModule("/js/voice.js").Setup<bool>("isSupported").SetResult(false);
        var cut = Render<PushToTalk>();
        cut.WaitForState(() => cut.Find(".mic-label").TextContent.Trim() is "Hold to talk"
            || cut.Markup.Contains("isn't supported"));
        return cut;
    }

    private void Capture(string mime = "audio/webm") =>
        Voice.Setup<PushToTalk.VoiceCapture?>("stop")
            .SetResult(new PushToTalk.VoiceCapture(OneByteBase64, mime, 1));

    [Fact]
    public void An_unsupported_browser_disables_the_button_and_says_why()
    {
        var cut = RenderMic(supported: false);
        Assert.True(cut.Find("button.mic").HasAttribute("disabled"));
        Assert.Contains("Voice input isn't supported in this browser.", cut.Find(".voice-status").TextContent);
    }

    [Fact]
    public async Task Hold_speak_release_runs_the_whole_loop_and_reads_the_reply_back()
    {
        Capture();
        Stt.Say("we're out of coffee");
        Chat.Next = new ChatResult { Success = true, Reply = "Noted — coffee marked out." };
        var applied = 0;
        var cut = Render<PushToTalk>(ps => ps.Add(p => p.OnApplied, () => applied++));
        cut.WaitForState(() => cut.Find(".mic-label").TextContent.Trim() == "Hold to talk");

        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button.mic").GetAttribute("aria-pressed")));
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        cut.WaitForAssertion(() =>
        {
            // The transcript is shown (trust: you see what it heard), the reply reads as a reply,
            // and the parent was told to refresh.
            Assert.Contains("“we're out of coffee”", cut.Find(".voice-heard").TextContent);
            Assert.Contains("Noted — coffee marked out.", cut.Find(".chat-reply").TextContent);
        });
        Assert.Equal(1, applied);
        Assert.Equal(["we're out of coffee"], Chat.Asked);
        // The read-back went through TTS and out the speaker.
        Assert.Contains(Tts.Spoken, s => s.Text == "Noted — coffee marked out.");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "play");
        // And the machine came home: ready for the next hold.
        Assert.Equal("Hold to talk", cut.Find(".mic-label").TextContent.Trim());
    }

    [Fact]
    public async Task Silence_is_answered_with_coaching_and_never_wakes_the_brain()
    {
        // The stop() capture comes back empty — releasing too fast. No STT, no chat, no cost.
        var cut = RenderMic();
        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        cut.WaitForAssertion(() =>
            Assert.Contains("Didn't catch anything — hold the button a beat longer.",
                cut.Find(".error").TextContent));
        Assert.Empty(Chat.Asked);
        Assert.Empty(Stt.Heard);
    }

    [Fact]
    public async Task An_unreadable_recording_apologizes_without_a_chat_call()
    {
        Capture();
        Stt.Results.Enqueue(new ShelfAware.Core.Speech.SpeechToTextResult { Success = false, Error = "garbled" });
        var cut = RenderMic();

        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        cut.WaitForAssertion(() =>
            Assert.Contains("Sorry — I couldn't make out the audio.", cut.Find(".error").TextContent));
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public async Task A_failed_chat_turn_styles_the_reply_as_an_error_and_skips_the_refresh()
    {
        Capture();
        Stt.Say("we're out of flurbium");
        Chat.Next = new ChatResult { Success = false, Reply = "I couldn't find that product." };
        var applied = 0;
        var cut = Render<PushToTalk>(ps => ps.Add(p => p.OnApplied, () => applied++));
        cut.WaitForState(() => cut.Find(".mic-label").TextContent.Trim() == "Hold to talk");

        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        cut.WaitForAssertion(() =>
            Assert.Contains("I couldn't find that product.", cut.Find(".error").TextContent));
        Assert.Equal(0, applied); // nothing changed, so nothing to refresh
    }

    [Fact]
    public async Task A_navigating_reply_moves_the_page_after_the_spoken_confirmation()
    {
        Capture();
        Stt.Say("show me the grocery list");
        Chat.Next = new ChatResult { Success = true, Reply = "Here's the grocery list.", NavigateTo = "/list" };
        var cut = RenderMic();

        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/list", nav.Uri));
        // The confirmation was spoken BEFORE the move — the navigation is the last thing that happens.
        Assert.Contains(Tts.Spoken, s => s.Text == "Here's the grocery list.");
    }

    [Fact]
    public async Task A_failed_read_back_is_a_bonus_lost_not_a_turn_lost()
    {
        Capture();
        Stt.Say("we're out of coffee");
        Tts.FailOn.Add("Noted");
        Chat.Next = new ChatResult { Success = true, Reply = "Noted — coffee marked out." };
        var cut = RenderMic();

        await cut.Find("button.mic").PointerDownAsync(new PointerEventArgs());
        await cut.Find("button.mic").PointerUpAsync(new PointerEventArgs());

        // The state change already applied and the reply is on screen; only the audio is missing.
        cut.WaitForAssertion(() =>
            Assert.Contains("Noted — coffee marked out.", cut.Find(".chat-reply").TextContent));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "play");
        Assert.Equal("Hold to talk", cut.Find(".mic-label").TextContent.Trim());
    }

    [Fact]
    public async Task The_keyboard_hold_guards_against_key_auto_repeat()
    {
        Capture();
        Stt.Say("hello there");
        var cut = RenderMic();
        var mic = cut.Find("button.mic");

        // A held space bar auto-repeats keydown — one recording must start, not one per repeat.
        await mic.KeyDownAsync(new KeyboardEventArgs { Key = " " });
        await cut.Find("button.mic").KeyDownAsync(new KeyboardEventArgs { Key = " " });
        await cut.Find("button.mic").KeyDownAsync(new KeyboardEventArgs { Key = " " });
        Assert.Single(JSInterop.Invocations.Where(i => i.Identifier == "start"));

        await cut.Find("button.mic").KeyUpAsync(new KeyboardEventArgs { Key = " " });
        cut.WaitForAssertion(() => Assert.Contains("hello there", cut.Find(".voice-heard").TextContent));
    }
}
