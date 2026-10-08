using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The dashboard's quick-update box, two guards that run before any charged call. The input cap is the
/// server-side half of <see cref="PromptInput"/> (the <c>maxlength</c> attribute is a courtesy a devtools
/// edit removes), refused with the cap named and the model never asked. The voice strip asks the ONE
/// "can this box hear?" definition the mic controls ask, so a deaf box shows neither a divider over an
/// empty slot nor a sentence pointing at an assistant that isn't in the corner.
/// </summary>
public class HomeInputGuardTests : PageTestContext
{
    private IRenderedComponent<Home> RenderHome()
    {
        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        return cut;
    }

    [Fact]
    public void An_over_long_update_is_refused_with_the_cap_named_and_chat_is_never_asked()
    {
        var cut = RenderHome();

        cut.Find("form input").Input(new string('x', PromptInput.ChatMaxLength + 1));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Contains(PromptInput.TooLongMessage(PromptInput.ChatMaxLength), cut.Find("[role=status]").TextContent));
        Assert.Contains("error", cut.Find("[role=status] p").GetAttribute("class"));
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public void An_update_at_the_cap_goes_through()
    {
        var cut = RenderHome();
        var atCap = new string('x', PromptInput.ChatMaxLength);

        cut.Find("form input").Input(atCap);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(atCap, Chat.Asked));
    }

    [Fact]
    public void The_input_states_its_own_limit()
    {
        var cut = RenderHome();
        Assert.Equal(PromptInput.ChatMaxLength.ToString(), cut.Find("form input").GetAttribute("maxlength"));
    }

    [Fact]
    public void A_hearing_box_offers_the_voice_strip()
    {
        var cut = RenderHome();
        Assert.Contains("or hold to talk", cut.Markup);
        Assert.Contains("Assistant in the corner", cut.Markup);
    }

    [Fact]
    public void A_deaf_box_offers_no_voice_strip_at_all()
    {
        // The demo box's shape: host keys authoritative, no voice key, no local ear.
        Voice.ApiKey = "";
        Voice.Managed = true;

        var cut = RenderHome();

        Assert.DoesNotContain("or hold to talk", cut.Markup);
        Assert.DoesNotContain("Assistant in the corner", cut.Markup);
    }
}
