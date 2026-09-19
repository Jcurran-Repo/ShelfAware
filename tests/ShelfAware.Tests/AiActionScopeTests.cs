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
    public async Task There_is_no_ambient_action_until_one_is_begun() =>
        Assert.Null(AiActionScope.Current);

    [Fact]
    public async Task A_begun_scope_is_the_ambient_one()
    {
        await using (AiActionScope.Begin(ServiceAction.ReceiptExtraction))
            Assert.Equal(ServiceAction.ReceiptExtraction, AiActionScope.Current?.Action);
    }

    [Fact]
    public async Task Disposing_restores_what_was_there_before_it()
    {
        await using (AiActionScope.Begin(ServiceAction.ChatTurn))
        {
            await using (AiActionScope.Begin(ServiceAction.RecipeAdapt))
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
    public async Task An_action_can_be_charged_exactly_once()
    {
        await using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

        Assert.True(scope.TryClaimCharge());   // the first provider round pays
        Assert.False(scope.TryClaimCharge());  // every later round rides along free
        Assert.False(scope.TryClaimCharge());
    }

    [Fact]
    public async Task A_nested_action_claims_separately_from_the_one_around_it()
    {
        // Both are real acts the household asked for, so both are charged — the claim is per SCOPE, not
        // per outermost action. The inner one paying must not consume the outer one's claim.
        await using var outer = AiActionScope.Begin(ServiceAction.ChatTurn);
        Assert.True(outer.TryClaimCharge());

        await using var inner = AiActionScope.Begin(ServiceAction.RecipeAdapt);
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
        await using (AiActionScope.Begin(ServiceAction.CensusPhoto))
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
    public async Task A_released_claim_lets_the_next_call_in_the_action_pay_instead()
    {
        // ⚠️ The claim is taken BEFORE the money is written — that is what stops two parallel rounds both
        // charging — so a write that fails having spent the claim would make every REMAINING round of the
        // action free too. One failed row would cost the whole action's charge, not one call's.
        await using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

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
        await Task.Run(async () =>
        {
            await using var _ = AiActionScope.Begin(ServiceAction.MealPlan);
        });

        Assert.Null(AiActionScope.Current);
    }

    // ------------------------------------------------------------------ the unit count

    [Fact]
    public async Task An_act_covers_one_unit_unless_it_says_otherwise()
    {
        // Almost every action is one thing the household asked for, so the count is the boring default and
        // CreditPricing.CreditsFor prices it per act. Only an action the price list prices BY THE UNIT may
        // pass a count at all — AiActionScopeSiteTests fails the build otherwise.
        await using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

        Assert.Equal(1, scope.Units);
    }

    [Fact]
    public async Task The_count_an_act_was_opened_with_is_the_count_it_is_charged_for()
    {
        // ⚠️ This is the whole per-meal price. A 21-meal plan carrying a Units of 1 is charged 1 credit for
        // seven credits of work, and nothing downstream could tell: the ledger line, the margin row and the
        // Settings quote would all agree with each other and all be wrong.
        await using var scope = AiActionScope.Begin(ServiceAction.MealPlan, units: 21);

        Assert.Equal(21, scope.Units);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task An_act_covering_nothing_still_covers_one(int units)
    {
        // A plan of no meals can't happen — SlotsFor always returns at least one — but the clamp is here
        // rather than at the call site so that a count arriving from somewhere new can never price an act
        // at zero, or (negatively) pay the household to run it.
        await using var scope = AiActionScope.Begin(ServiceAction.MealPlan, units: units);

        Assert.Equal(1, scope.Units);
    }

    [Fact]
    public async Task An_act_knows_whether_its_one_charge_has_been_taken()
    {
        // ⚠️ This is what lets the credit gate tell "this household is about to spend" from "this household
        // already has". A meal plan pays its whole price on the first of eighteen calls, so a gate that
        // re-asked "can they afford this act?" on call two would refuse the rest of a plan they had paid
        // for in full — and the page would report the fraction that got through as a success.
        await using var scope = AiActionScope.Begin(ServiceAction.MealPlan, units: 21);
        Assert.False(scope.ChargeClaimed);

        Assert.True(scope.TryClaimCharge());
        Assert.True(scope.ChargeClaimed);

        scope.ReleaseCharge();
        Assert.False(scope.ChargeClaimed); // handed back, so the next round may pay instead
    }

    [Fact]
    public async Task Asking_whether_the_charge_is_claimed_does_not_claim_it()
    {
        // It is a READ. If it took the charge the way TryClaimCharge does, every gated call would consume
        // the act's one charge before the money write ever ran, and nothing would ever be billed.
        await using var scope = AiActionScope.Begin(ServiceAction.ChatTurn);

        Assert.False(scope.ChargeClaimed);
        Assert.False(scope.ChargeClaimed);

        Assert.True(scope.TryClaimCharge()); // still there to be taken
    }

    // ------------------------------------------------------------------ settling what was delivered

    [Fact]
    public async Task An_act_that_says_nothing_is_taken_to_have_delivered_nothing()
    {
        // ⚠️ The default, and it is this way round on purpose. The paths that skip Delivered() are the ones
        // that threw, and an act that threw delivered nothing. An act that merely FORGETS is caught by
        // AiActionScopeSiteTests at build time rather than by a household reading its ledger.
        var settled = -1;
        await using (var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 124))
            act.ChargeRecorded(42, (delivered, _) => { settled = delivered; return Task.CompletedTask; });

        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task What_an_act_delivered_is_what_it_settles_for()
    {
        var settled = -1;
        await using (var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 124))
        {
            act.ChargeRecorded(42, (delivered, _) => { settled = delivered; return Task.CompletedTask; });
            act.Delivered(7);
        }

        Assert.Equal(7, settled);
    }

    [Fact]
    public async Task An_act_that_delivered_everything_it_asked_for_settles_nothing()
    {
        // Nothing to give back, so nothing is written — the ledger stays a record of money moving, not a
        // log of every act that went well.
        var settled = false;
        await using (var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 124))
        {
            act.ChargeRecorded(42, (_, _) => { settled = true; return Task.CompletedTask; });
            act.Delivered(124);
        }

        Assert.False(settled);
    }

    [Fact]
    public async Task An_act_cannot_claim_to_have_delivered_more_than_it_asked_for()
    {
        // ⚠️ A count above Units is clamped to Units, which reads as "delivered in full" and settles
        // nothing. Left unclamped it would price the refund BELOW zero — a reversal that charged the
        // household a second time, from a bug in a service rather than anywhere near the money code.
        var settled = -1;
        await using (var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 7))
        {
            act.ChargeRecorded(3, (delivered, _) => { settled = delivered; return Task.CompletedTask; });
            act.Delivered(9_999);

            Assert.Equal(7, act.UnitsDelivered); // clamped to what was asked for
        }

        Assert.Equal(-1, settled); // never called: nothing was owed back
    }

    [Fact]
    public async Task A_negative_delivery_reads_as_nothing_delivered()
    {
        var settled = -1;
        await using (var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 7))
        {
            act.ChargeRecorded(3, (delivered, _) => { settled = delivered; return Task.CompletedTask; });
            act.Delivered(-4);
        }

        Assert.Equal(0, settled);
    }

    [Fact]
    public async Task An_act_nobody_charged_settles_nothing()
    {
        // A Founder, a BYOK circuit, a box with billing off: no charge landed, so ChargeRecorded was never
        // called and there is nothing to give back. A refund here would MINT credit out of a charge that
        // never existed.
        await using var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 124);
        act.Delivered(0);

        Assert.False(act.HasSettlement); // and disposal below settles nothing, with nothing to settle to
    }

    [Fact]
    public async Task The_enclosing_act_is_restored_even_when_settling_throws()
    {
        // ⚠️ An ambient scope outliving its act would charge the NEXT unlabelled call to it — a worse
        // outcome than a refund that has to be chased in the log.
        await using var outer = AiActionScope.Begin(ServiceAction.ChatTurn);

        var inner = AiActionScope.Begin(ServiceAction.MealPlan, units: 4);
        inner.ChargeRecorded(2, (_, _) => Task.FromException(new InvalidOperationException("auth.db is gone")));

        // ⚠️ Called straight from the test body, and the restore asserted BEFORE the settlement is
        // awaited. Both halves are deliberate: DisposeAsync restores synchronously (see its comment),
        // and wrapping this call in the `async` lambda Assert.ThrowsAsync wants would run the restore
        // inside that lambda's copied execution context, where an AsyncLocal write does not flow back
        // out to here — the assertion below would then read the ambient scope this test never touched
        // and pass over a real leak.
        var settling = inner.DisposeAsync();
        Assert.Same(outer, AiActionScope.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await settling);
    }

    [Fact]
    public async Task A_charge_recorded_after_the_act_closed_is_refused()
    {
        // ⚠️ Loud, not ignored. By this point DisposeAsync has taken the settlement and gone, so a callback
        // attached here could never run: the household would be charged with the refund already
        // unreachable, and nothing downstream would ever mention it. A provider call outliving the scope
        // that started it is the leak shape this type's own remarks describe; it used to cost a free call,
        // and since the refund it would cost real money in the silent direction.
        var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 4);
        await act.DisposeAsync();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => act.ChargeRecorded(2, (_, _) => Task.CompletedTask));

        // The message has to name both, because it is the only record that will exist: how much is
        // stranded, and which act stranded it. An operator reading "an act has already closed" alone
        // cannot tell a free tag suggestion from a 42-credit meal plan.
        Assert.Contains("2 credit(s)", thrown.Message);
        Assert.Contains(nameof(ServiceAction.MealPlan), thrown.Message);
        // And the diagnosis, not just the symptom: "an act has already closed" tells whoever finds this in
        // a log nothing about what to go and look for.
        Assert.Contains("outlived its scope", thrown.Message);
    }

    [Fact]
    public async Task An_act_cannot_record_a_charge_with_no_way_to_give_it_back()
    {
        // A null settlement would read downstream as "nothing was charged" — indistinguishable from a
        // Founder — and the act would close having silently kept money it could not return.
        await using var act = AiActionScope.Begin(ServiceAction.MealPlan, units: 4);

        Assert.Throws<ArgumentNullException>(() => act.ChargeRecorded(2, null!));
        Assert.False(act.HasSettlement);
    }

    [Fact]
    public async Task Disposing_twice_does_not_reinstate_a_scope_that_has_itself_closed()
    {
        // ⚠️ The settlement was already once-only; the RESTORE was not. Disposing inner, then outer, then
        // inner again would put `outer` back as the ambient scope after outer had closed — and the next
        // unlabelled AI call on that flow would be charged to a dead act whose one charge is already
        // claimed, which is to say charged to nobody at all.
        var outer = AiActionScope.Begin(ServiceAction.ChatTurn);
        var inner = AiActionScope.Begin(ServiceAction.MealPlan, units: 4);

        await inner.DisposeAsync();
        await outer.DisposeAsync();
        await inner.DisposeAsync();  // the stray second close

        Assert.Null(AiActionScope.Current);
    }
}
