using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>The ONE surface-side AI-availability decision (phase 4c): every page/voice pre-check asks
/// <see cref="AiErrorText.BlockedReasonAsync"/>, so its four answers are pinned here once. Managed reads the
/// credit entitlement (out of credits → say so); BYOK/self-host reads whether a key is present (none → say
/// so); allowed either way returns null. The exact wording is asserted so a message edit can't drift silently.</summary>
public class AiErrorTextTests
{
    private static CircuitAiSettings Managed() =>
        new(Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "server-key" }));

    private static CircuitAiSettings Byok(string key) =>
        new(Options.Create(new LlmOptions { KeyMode = "byok", ApiKey = key }));

    [Fact]
    public async Task Managed_with_credit_is_allowed()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 5_000_000 }, Managed());

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_and_unlimited_tier_is_allowed_even_at_zero_balance()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Founder) { BalanceMicros = 0 }, Managed());

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_with_no_credit_reports_out_of_credits()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 0 }, Managed());

        Assert.Equal(AiErrorText.OutOfCredits, reason);
    }

    [Fact]
    public async Task Byok_with_a_key_is_allowed_and_never_consults_the_balance()
    {
        // A BYOK circuit pays its own way, so an empty managed balance must not gate it.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 0 }, Byok("sk-visitor"));

        Assert.Null(reason);
    }

    [Fact]
    public async Task Byok_with_no_key_reports_no_ai_configured()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 5_000_000 }, Byok(""));

        Assert.Equal(AiErrorText.NoKey, reason);
    }
}
