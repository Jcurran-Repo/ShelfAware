using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The public data-handling page describes the DEPLOYMENT it runs on, from the same options the code
/// reads — a managed box names the host's provider, a BYOK box says the key is the visitor's, and only a
/// box with a demo valve configured calls itself a shared demo. One page that is true on every box
/// rather than one paragraph that is wrong on all but one.
/// </summary>
public class PrivacyPageTests : PageTestContext
{
    private LlmOptions _llm = new();
    private DemoOptions _demo = new();

    protected override void RegisterAdditionalServices()
    {
        // Transient, read at render time: a test sets the field and then renders, and one test renders
        // twice with different options.
        Services.AddTransient<IOptions<LlmOptions>>(_ => Options.Create(_llm));
        Services.AddTransient<IOptions<DemoOptions>>(_ => Options.Create(_demo));
    }

    [Fact]
    public void A_managed_box_names_the_hosts_provider()
    {
        _llm = new LlmOptions { KeyMode = "managed", ApiKey = "sk-host", Provider = "Anthropic" };

        var text = Render<Privacy>().Markup;

        Assert.Contains("run on <strong>Anthropic</strong>", text);
        Assert.DoesNotContain("provider <strong>you</strong> pick", text);
    }

    [Fact]
    public void A_byok_box_says_the_key_is_the_visitors_and_never_stored()
    {
        _llm = new LlmOptions { KeyMode = "byok" };

        var text = Render<Privacy>().Markup;

        Assert.Contains("provider <strong>you</strong> pick", text);
        Assert.Contains("never written to a database or a log", text);
    }

    [Fact]
    public void Only_a_box_with_a_demo_valve_calls_itself_a_shared_demo()
    {
        Assert.DoesNotContain("shared demo box", Render<Privacy>().Markup);

        _demo = new DemoOptions { DailyGlobalCallLimit = 300 };
        Assert.Contains("shared demo box", Render<Privacy>().Markup);
    }

    [Fact]
    public void It_is_honest_about_what_delete_does_not_reach()
    {
        var text = Render<Privacy>().Markup;

        Assert.Contains("What delete does not reach", text);
        Assert.Contains("no self-service account deletion yet", text);
        Assert.Contains("Receipt images are not aged out", text);
    }
}
