using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Phase 4c wiring: when a MANAGED household's credit balance is spent, an AI surface must SAY the real
/// reason up front (<see cref="AiErrorText.OutOfCredits"/>) and NOT attempt the doomed call — the services
/// fail soft, so a swallowed refusal would otherwise read as a generic "try again". The DECISION lives in
/// <see cref="AiErrorText.BlockedReasonAsync"/> (unit-tested across all four states in AiErrorTextTests);
/// these pin that the two flagship surfaces WIRE it — show the reason AND skip the AI call. The other
/// surfaces (Recipes adapt/swap, ProductDetail substitutes, Cookbook tags, RecipeImport, and the voice
/// surfaces) make the identical pre-check call and are live-verified.
/// </summary>
public class AiUnavailableSurfaceTests : PageTestContext
{
    protected override void RegisterAdditionalServices()
    {
        // A managed deployment (the server key is authoritative), out of credits — the exact state the
        // token gate enforces. This overrides the base harness's AI-available default.
        Services.AddSingleton(new CircuitAiSettings(Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "server-key" })));
        Services.AddSingleton<IEntitlements>(new FakeEntitlements()); // Free, zero balance → not allowed
    }

    [Fact]
    public void Home_chat_reports_out_of_credits_and_never_calls_the_brain()
    {
        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll("form").Count > 0);

        cut.Find("form input").Input("we're out of coffee");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
            Assert.Equal(AiErrorText.OutOfCredits, cut.Find("[role=status] p").TextContent.Trim()));
        // It's an error, not a hint or a reply — and, the mutation-killer, the model was never asked.
        Assert.Contains("error", cut.Find("[role=status] p").GetAttribute("class"));
        Assert.Empty(Chat.Asked);
    }

    [Fact]
    public void Recipes_get_ideas_reports_out_of_credits_and_never_calls_the_advisor()
    {
        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);

        cut.Find("input[aria-label=\"Describe what you're in the mood for\"]").Input("something fast");
        cut.FindAll("form").First(f => f.QuerySelector("button")?.TextContent.Contains("Get ideas") == true).Submit();

        cut.WaitForAssertion(() =>
            Assert.Equal(AiErrorText.OutOfCredits, cut.Find("p.error").TextContent.Trim()));
        Assert.Null(SuggestionAdvisor.LastOnHand); // the advisor was never asked
    }
}
