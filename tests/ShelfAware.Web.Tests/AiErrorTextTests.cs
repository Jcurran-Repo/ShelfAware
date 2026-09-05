using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>The ONE surface-side AI-availability decision (phase 4c): every page/voice pre-check asks
/// <see cref="AiErrorText.BlockedReasonAsync"/>, so its answers are pinned here once. The box-wide demo valve
/// is checked first (a capped demo box → come back tomorrow); then managed reads the credit entitlement (out
/// of credits → say so); a BYOK/self-host circuit reads whether a key is present (none → say so); allowed
/// returns null. The exact wording is asserted so a message edit can't drift silently.</summary>
public class AiErrorTextTests
{
    private static CircuitAiSettings Managed() =>
        new(Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "server-key" }));

    private static CircuitAiSettings Byok(string key) =>
        new(Options.Create(new LlmOptions { KeyMode = "byok", ApiKey = key }));

    // The box-wide demo valve. Most tests use one that never blocks (the family / self-host default), so they
    // exercise the credit/key logic; the two demo tests below use a blocking one.
    private sealed record FakeDemoValve(string? Message) : IDemoValve
    {
        public ValueTask<string?> CallBlockedMessageAsync(CancellationToken ct = default) => new(Message);
    }

    private static IDemoValve NotBlocked() => new FakeDemoValve(null);

    [Fact]
    public async Task Managed_with_credit_is_allowed()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 5_000_000 }, Managed(), NotBlocked());

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_and_unlimited_tier_is_allowed_even_at_zero_balance()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Founder) { BalanceMicros = 0 }, Managed(), NotBlocked());

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_Aware_with_no_credit_says_top_up()
    {
        // An Aware subscriber CAN buy a credit pack, so the out-of-credits message names that.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Aware) { BalanceMicros = 0 }, Managed(), NotBlocked());

        Assert.Equal(AiErrorText.OutOfCredits, reason);
    }

    [Fact]
    public async Task Managed_Free_with_no_credit_says_subscribe()
    {
        // A Free household CANNOT buy packs (subscribers-only), so it's told to subscribe, not "add a pack"
        // (item 36: never name an act the household can't take).
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Free) { BalanceMicros = 0 }, Managed(), NotBlocked());

        Assert.Equal(AiErrorText.SubscribeToUse, reason);
    }

    [Fact]
    public async Task Managed_but_the_demo_box_is_capped_says_come_back_and_beats_the_credit_check()
    {
        // The box-wide demo valve is checked BEFORE credits (mirroring the server-side gate order), so even a
        // household with a positive balance is told to come back tomorrow once the whole box has hit its cap.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 5_000_000 }, Managed(),
            new FakeDemoValve(DemoLimits.DailyCapReachedMessage));

        Assert.Equal(DemoLimits.DailyCapReachedMessage, reason);
    }

    [Fact]
    public async Task Byok_never_consults_the_demo_valve()
    {
        // The box-wide valve caps the HOST's key; a BYOK visitor rides their own, so a capped box must not
        // gate them.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 0 }, Byok("sk-visitor"),
            new FakeDemoValve(DemoLimits.DailyCapReachedMessage));

        Assert.Null(reason);
    }

    [Fact]
    public async Task Byok_with_a_key_is_allowed_and_never_consults_the_balance()
    {
        // A BYOK circuit pays its own way, so an empty managed balance must not gate it.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 0 }, Byok("sk-visitor"), NotBlocked());

        Assert.Null(reason);
    }

    [Fact]
    public async Task Byok_with_no_key_reports_no_ai_configured()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceMicros = 5_000_000 }, Byok(""), NotBlocked());

        Assert.Equal(AiErrorText.NoKey, reason);
    }
}
