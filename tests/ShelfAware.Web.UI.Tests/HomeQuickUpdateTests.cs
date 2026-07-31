using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Chat;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Quick update box (v4.2's Enter-submits fix). The browser's implicit form submission —
/// Enter in the input — no-ops when the form's default submit button is disabled, and enabling
/// it on the first keystroke rides a circuit round-trip, so "type, Enter" lost the race and did
/// nothing in silence. The rule under test: the button disables on BUSY only, never on a blank
/// box, and a blank send answers with a hint instead of an active-looking button doing nothing.
/// </summary>
public class HomeQuickUpdateTests : PageTestContext
{
    private IRenderedComponent<Home> RenderHome()
    {
        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        return cut;
    }

    [Fact]
    public void The_submit_button_is_enabled_while_the_box_is_blank()
    {
        // Enabled-on-blank is the load-bearing half: Enter's implicit submission needs a live
        // default button at the moment of the keystroke, before any round-trip.
        var cut = RenderHome();
        var button = cut.Find("form button[type=submit]");
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("Send", button.TextContent.Trim());
    }

    [Fact]
    public void A_blank_send_answers_with_a_hint_and_never_calls_chat()
    {
        var cut = RenderHome();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains("Say what you bought or ran low on first", cut.Find("[role=status]").TextContent));
        // The hint is guidance, not an error — and the model was never asked.
        Assert.Contains("chat-reply", cut.Find("[role=status] p").GetAttribute("class"));
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public void The_button_disables_while_a_send_is_in_flight_and_recovers_after()
    {
        var cut = RenderHome();
        var pending = new TaskCompletionSource<ChatResult>();
        Chat.Hold = pending;

        cut.Find("form input").Input("we're out of coffee");
        cut.Find("form").Submit();

        // Mid-flight: the busy state is what disables the button (the only thing allowed to).
        cut.WaitForAssertion(() =>
        {
            var busy = cut.Find("form button[type=submit]");
            Assert.True(busy.HasAttribute("disabled"));
            Assert.Equal("Working…", busy.TextContent.Trim());
        });

        pending.SetResult(new ChatResult { Success = true, Reply = "Noted — coffee marked out." });

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("form button[type=submit]");
            Assert.False(button.HasAttribute("disabled"));
            Assert.Equal("Send", button.TextContent.Trim());
        });
        Assert.Equal(["we're out of coffee"], Chat.Asked);
    }

    [Fact]
    public void A_successful_send_shows_the_reply_and_clears_the_box()
    {
        Chat.Next = new ChatResult { Success = true, Reply = "Noted — coffee marked out." };
        var cut = RenderHome();

        cut.Find("form input").Input("we're out of coffee");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains("Noted — coffee marked out.", cut.Find("[role=status]").TextContent));
        Assert.Contains("chat-reply", cut.Find("[role=status] p").GetAttribute("class"));
        // The box clears only on success, ready for the next update.
        Assert.Equal("", cut.Find("form input").GetAttribute("value") ?? "");
    }

    [Fact]
    public void A_failed_send_shows_the_reply_as_an_error_and_keeps_the_text()
    {
        Chat.Next = new ChatResult { Success = false, Reply = "I couldn't find that product." };
        var cut = RenderHome();

        cut.Find("form input").Input("we're out of flurbium");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains("I couldn't find that product.", cut.Find("[role=status]").TextContent));
        Assert.Contains("error", cut.Find("[role=status] p").GetAttribute("class"));
        // The text stays for a correction — clearing it would make the user retype everything.
        Assert.Equal("we're out of flurbium", cut.Find("form input").GetAttribute("value"));
    }

    [Fact]
    public void A_navigating_reply_sends_the_page_where_the_tool_asked()
    {
        Chat.Next = new ChatResult { Success = true, Reply = "Here's the grocery list.", NavigateTo = "/list" };
        var cut = RenderHome();

        cut.Find("form input").Input("show me the grocery list");
        cut.Find("form").Submit();

        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/list", nav.Uri));
    }
}
