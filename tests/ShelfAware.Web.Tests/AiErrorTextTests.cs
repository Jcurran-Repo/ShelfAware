using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
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
            new FakeEntitlements { BalanceCredits = 5_000_000 }, Managed(), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_and_unlimited_tier_is_allowed_even_at_zero_balance()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Founder) { BalanceCredits = 0 }, Managed(), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Managed_Aware_with_no_credit_says_top_up()
    {
        // An Aware subscriber CAN buy a credit pack, so the out-of-credits message names that.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Aware) { BalanceCredits = 0 }, Managed(), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Equal(AiErrorText.OutOfCredits, reason);
    }

    [Fact]
    public async Task Managed_Free_with_no_credit_says_subscribe()
    {
        // A Free household CANNOT buy packs (subscribers-only), so it's told to subscribe, not "add a pack"
        // (item 36: never name an act the household can't take).
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Free) { BalanceCredits = 0 }, Managed(), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Equal(AiErrorText.SubscribeToUse, reason);
    }

    [Fact]
    public async Task Managed_but_the_demo_box_is_capped_says_come_back_and_beats_the_credit_check()
    {
        // The box-wide demo valve is checked BEFORE credits (mirroring the server-side gate order), so even a
        // household with a positive balance is told to come back tomorrow once the whole box has hit its cap.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceCredits = 5_000_000 }, Managed(),
            new FakeDemoValve(DemoLimits.DailyCapReachedMessage), ServiceAction.ChatTurn);

        Assert.Equal(DemoLimits.DailyCapReachedMessage, reason);
    }

    [Fact]
    public async Task Byok_never_consults_the_demo_valve()
    {
        // The box-wide valve caps the HOST's key; a BYOK visitor rides their own, so a capped box must not
        // gate them.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceCredits = 0 }, Byok("sk-visitor"),
            new FakeDemoValve(DemoLimits.DailyCapReachedMessage), ServiceAction.ChatTurn);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Byok_with_a_key_is_allowed_and_never_consults_the_balance()
    {
        // A BYOK circuit pays its own way, so an empty managed balance must not gate it.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceCredits = 0 }, Byok("sk-visitor"), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Null(reason);
    }

    [Fact]
    public async Task Byok_with_no_key_reports_no_ai_configured()
    {
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements { BalanceCredits = 5_000_000 }, Byok(""), NotBlocked(), ServiceAction.ChatTurn);

        Assert.Equal(AiErrorText.NoKey, reason);
    }

    [Fact]
    public async Task A_balance_that_covers_something_but_not_this_says_so_rather_than_out_of_credits()
    {
        // ⚠️ THE finding the second gate pass caught. A ChatTurn is 2 credits; this household holds 1. It is
        // not out of credits and its trial is not used up — every cheaper action on the site still works —
        // so "You've used up the free AI trial" is a false statement and points at the wrong remedy.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Free) { BalanceCredits = 1 }, Managed(), NotBlocked(),
            ServiceAction.ChatTurn);

        Assert.NotEqual(AiErrorText.SubscribeToUse, reason);
        Assert.NotEqual(AiErrorText.OutOfCredits, reason);
        Assert.Contains("needs 2 credits", reason);
        Assert.Contains("you have 1 credit", reason);
    }

    [Fact]
    public async Task What_a_big_act_needs_is_quoted_from_the_act_not_from_a_flat_price()
    {
        // A meal plan is priced by the meal, so the shortfall message has to price the plan the household
        // actually asked for — the same CreditsFor the charge will read, over the same unit count.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Aware) { BalanceCredits = 41 }, Managed(), NotBlocked(),
            ServiceAction.MealPlan, units: 124);

        Assert.Contains("needs 42 credits", reason);
        Assert.Contains("you have 41 credits", reason);
        Assert.Contains("credit pack", reason);   // an Aware household can buy one
    }

    [Fact]
    public async Task A_household_with_nothing_at_all_is_still_told_it_is_spent()
    {
        // The older message is still the right one at zero: there is no smaller act to suggest, so the
        // only thing to say is how to get more.
        var reason = await AiErrorText.BlockedReasonAsync(
            new FakeEntitlements(HouseholdTier.Aware) { BalanceCredits = 0 }, Managed(), NotBlocked(),
            ServiceAction.MealPlan, units: 124);

        Assert.Equal(AiErrorText.OutOfCredits, reason);
    }

    [Fact]
    public async Task The_pre_check_and_the_server_gate_answer_the_same_question()
    {
        // ⚠️ The biconditional, which is what the half-conversion broke: the surface said go and the gate
        // said no, so the household was told the assistant was broken. Both now ask CheckAiAsync with the
        // act, so asserting them equal across the boundary is asserting they cannot drift apart again.
        foreach (var balance in new long[] { 0, 1, 2, 41, 42, 43 })
            foreach (var (act, units) in new[]
                     {
                         (ServiceAction.ChatTurn, 1), (ServiceAction.MealPlan, 124),
                         (ServiceAction.TagSuggest, 1), (ServiceAction.MealPlan, 7),
                     })
            {
                var entitlements = new FakeEntitlements(HouseholdTier.Free) { BalanceCredits = balance };
                var surfaceAllows = await AiErrorText.BlockedReasonAsync(
                    entitlements, Managed(), NotBlocked(), act, units) is null;
                var gateAllows = (await entitlements.CheckAiAsync(act, units)).Allowed;

                Assert.Equal(gateAllows, surfaceAllows);
            }
    }
}
