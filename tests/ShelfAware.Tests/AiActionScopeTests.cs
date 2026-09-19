using ShelfAware.Core.Billing;

namespace ShelfAware.Tests;

/// <summary>
/// The ambient scope that tells the charge point WHICH act it is serving. It exists because the two
/// facts live in different places: only the service knows an action's boundary, and only the metering
/// decorator sees the provider call. Everything pinned here is a property the price list depends on —
/// if a chat turn's five tool rounds each charged, "a chat turn is 2 credits" would be false.
/// </summary>
public class AiActionScopeTests
{
    [Fact]
    public void There_is_no_ambient_action_until_one_is_begun() =>
        Assert.Null(AiActionScope.Current);

    [Fact]
    public void A_begun_scope_is_the_ambient_one()
    {
        using (AiActionScope.Begin(ServiceAction.ReceiptExtraction))
            Assert.Equal(ServiceAction.ReceiptExtraction, AiActionScope.Current?.Action);
    }

    [Fact]
    public void Disposing_restores_what_was_there_before_it()
    {
        using (AiActionScope.Begin(ServiceAction.ChatTurn))
        {
            using (AiActionScope.Begin(ServiceAction.RecipeAdapt))
                Assert.Equal(ServiceAction.RecipeAdapt, AiActionScope.Current?.Action);

            // ⚠️ Restores the ENCLOSING scope, not null. A chat turn that adapts a recipe through a tool
            // is really two acts, and the turn must go on being an act after the inner one ends — clearing
            // to null here would make every provider call after the tool unlabelled, and charge the turn
            // by cost instead of by its published price.
            Assert.Equal(ServiceAction.ChatTurn, AiActionScope.Current?.Action);
        }

        Assert.Null(AiActionScope.Current);
    }

    [Fact]
    public void An_action_can_be_charged_exactly_once()
    {
        using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

        Assert.True(scope.TryClaimCharge());   // the first provider round pays
        Assert.False(scope.TryClaimCharge());  // every later round rides along free
        Assert.False(scope.TryClaimCharge());
    }

    [Fact]
    public void A_nested_action_claims_separately_from_the_one_around_it()
    {
        // Both are real acts the household asked for, so both are charged — the claim is per SCOPE, not
        // per outermost action. The inner one paying must not consume the outer one's claim.
        using var outer = AiActionScope.Begin(ServiceAction.ChatTurn);
        Assert.True(outer.TryClaimCharge());

        using var inner = AiActionScope.Begin(ServiceAction.RecipeAdapt);
        Assert.True(inner.TryClaimCharge());
    }

    // ⚠️ There is deliberately NO test that the claim holds under a RACE, and the gap is real rather
    // than an oversight. A racing version was written and then removed: with real threads released from one
    // gate and the race re-run 200 times, it killed a deliberately non-atomic claim in only four runs of
    // six. A test whose kill is a coin flip claims coverage it does not have AND will eventually fail on CI
    // for reasons no change caused — strictly worse than saying so. The atomicity is held by reading
    // Interlocked.Exchange in AiActionScope, and by the test above proving the claim refuses a second time.

    [Fact]
    public async Task The_action_flows_into_the_work_it_starts()
    {
        // The whole point of an AMBIENT scope: the services that declare an action don't hand it down as
        // a parameter — it has to reach a provider call several awaits deep without anyone passing it.
        using (AiActionScope.Begin(ServiceAction.CensusPhoto))
        {
            await Task.Yield();
            await Task.Run(async () =>
            {
                await Task.Delay(1);
                Assert.Equal(ServiceAction.CensusPhoto, AiActionScope.Current?.Action);
            });
        }
    }

    [Fact]
    public void A_released_claim_lets_the_next_call_in_the_action_pay_instead()
    {
        // ⚠️ The claim is taken BEFORE the money is written — that is what stops two parallel rounds both
        // charging — so a write that fails having spent the claim would make every REMAINING round of the
        // action free too. One failed row would cost the whole action's charge, not one call's.
        using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

        Assert.True(scope.TryClaimCharge());
        Assert.False(scope.TryClaimCharge());   // as it should be while the claim stands

        scope.ReleaseCharge();

        Assert.True(scope.TryClaimCharge());    // the next round can pay
        Assert.False(scope.TryClaimCharge());   // and having paid, it is claimed again
    }

    [Fact]
    public async Task An_action_begun_in_a_side_task_does_not_leak_into_its_caller()
    {
        // The other half of "flows DOWN only". Two households' work runs on one server; an action
        // escaping upward would label — and price — a call that belongs to something else entirely.
        await Task.Run(() =>
        {
            using var _ = AiActionScope.Begin(ServiceAction.MealPlan);
        });

        Assert.Null(AiActionScope.Current);
    }
}
