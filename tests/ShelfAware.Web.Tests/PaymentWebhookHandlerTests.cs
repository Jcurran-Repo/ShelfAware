using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The webhook effect-applier (phase 3 step 2). Runs against real SQLite (TestAuthDb) because every claim
/// is about what actually lands in auth.db — the tier/period on Household, the ledger entries, and the
/// idempotency row — in one transaction. The endpoint (signature verify + HTTP) is live-verified; this pins
/// the effects.
/// </summary>
public class PaymentWebhookHandlerTests : IDisposable
{
    private readonly TestAuthDb _auth = new();
    private readonly RecordingProvider _provider = new();

    public void Dispose() => _auth.Dispose();

    private PaymentWebhookHandler Handler() => new(_auth, _provider, NullLogger<PaymentWebhookHandler>.Instance);

    /// <summary>Records which subscriptions the handler asked the provider to cancel — the supersede
    /// (purchaser-departure) behaviour is the one thing the handler drives through the provider API.</summary>
    private sealed class RecordingProvider : IPaymentProvider
    {
        public List<string> Cancelled { get; } = [];
        public PaymentProviderKind Kind => PaymentProviderKind.Fake;
        public string SignatureHeaderName => "X-Fake-Signature";
        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CheckoutSession("/fake"));
        public Task<string> CreatePortalUrlAsync(string billingCustomerId, string returnUrl, CancellationToken ct = default) =>
            Task.FromResult("/fake");
        public WebhookParse ParseWebhook(string payload, string? signatureHeader) => WebhookParse.Invalid;
        public Task CancelSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
        {
            Cancelled.Add(subscriptionId);
            return Task.CompletedTask;
        }
    }

    private async Task<Household> SeedAsync(Action<Household>? setup = null)
    {
        await using var db = _auth.CreateDbContext();
        var household = new Household { Name = "Test" };
        setup?.Invoke(household);
        db.Households.Add(household);
        await db.SaveChangesAsync();
        return household;
    }

    private async Task<Household> ReloadAsync(string id)
    {
        await using var db = _auth.CreateDbContext();
        return await db.Households.AsNoTracking().SingleAsync(h => h.Id == id);
    }

    private Task<long> BalanceAsync(string householdId) => new CreditLedger(_auth).GetBalanceMicrosAsync(householdId);

    private static readonly DateTimeOffset PeriodEnd = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Checkout_of_a_subscription_makes_the_household_Aware()
    {
        var household = await SeedAsync(); // Free by default
        var evt = new PaymentWebhookEvent("evt_sub", PaymentEventKind.CheckoutCompleted,
            HouseholdId: household.Id, BillingCustomerId: "cus_1", SubscriptionId: "sub_1",
            Product: BillingProduct.SubscriptionMonthly, PeriodEnd: PeriodEnd);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal(HouseholdTier.Aware, after.Tier);
        Assert.Equal("cus_1", after.BillingCustomerId);
        Assert.Equal("sub_1", after.SubscriptionId);
        Assert.Equal(PeriodEnd, after.SubscriptionRenewsAt);
        Assert.False(after.SubscriptionCancelAtPeriodEnd);
    }

    [Fact]
    public async Task Checkout_of_a_credit_pack_grants_the_amount_to_the_ledger()
    {
        var household = await SeedAsync(h => h.Tier = HouseholdTier.Aware); // packs are subscribers-only
        var evt = new PaymentWebhookEvent("evt_pack", PaymentEventKind.CheckoutCompleted,
            HouseholdId: household.Id, BillingCustomerId: "cus_1",
            Product: BillingProduct.CreditPack10, AmountMicros: 10_000_000);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        Assert.Equal(10_000_000, await BalanceAsync(household.Id));
        Assert.Equal(HouseholdTier.Aware, (await ReloadAsync(household.Id)).Tier); // a pack doesn't change the tier
    }

    [Fact]
    public async Task A_renewal_resolves_by_subscription_id_extends_the_period_and_keeps_Aware()
    {
        // No HouseholdId on the event — a real provider's renewal carries only the subscription id, so this
        // exercises the fallback resolution AND that a renewal keeps the tier while moving the period.
        var old = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.SubscriptionId = "sub_9";
            h.SubscriptionRenewsAt = old;
        });
        var evt = new PaymentWebhookEvent("evt_renew", PaymentEventKind.SubscriptionRenewed,
            SubscriptionId: "sub_9", PeriodEnd: PeriodEnd);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal(HouseholdTier.Aware, after.Tier);
        Assert.Equal(PeriodEnd, after.SubscriptionRenewsAt);
    }

    [Fact]
    public async Task Cancel_at_period_end_keeps_Aware_but_flags_it()
    {
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.SubscriptionId = "sub_c";
        });
        var evt = new PaymentWebhookEvent("evt_upd", PaymentEventKind.SubscriptionUpdated,
            SubscriptionId: "sub_c", PeriodEnd: PeriodEnd, CancelAtPeriodEnd: true);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal(HouseholdTier.Aware, after.Tier); // still paid until the period ends
        Assert.True(after.SubscriptionCancelAtPeriodEnd);
    }

    [Fact]
    public async Task A_cancelled_subscription_drops_to_Free_but_keeps_the_customer_id_and_credits()
    {
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.BillingCustomerId = "cus_keep";
            h.SubscriptionId = "sub_end";
            h.SubscriptionRenewsAt = PeriodEnd;
        });
        await new CreditLedger(_auth).GrantAsync(household.Id, 5_000_000, "seed"); // purchased credits on file

        var evt = new PaymentWebhookEvent("evt_cancel", PaymentEventKind.SubscriptionCancelled,
            SubscriptionId: "sub_end");

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal(HouseholdTier.Free, after.Tier);       // the sub ended — posture drop, nothing deleted
        Assert.Null(after.SubscriptionId);
        Assert.Null(after.SubscriptionRenewsAt);
        Assert.False(after.SubscriptionCancelAtPeriodEnd);
        Assert.Equal("cus_keep", after.BillingCustomerId);  // kept for re-subscribe / portal
        Assert.Equal(5_000_000, await BalanceAsync(household.Id)); // credits survive a tier drop (they were bought)
    }

    [Fact]
    public async Task A_refund_resolves_by_customer_id_and_the_balance_can_go_negative()
    {
        var household = await SeedAsync(h => h.BillingCustomerId = "cus_r");
        await new CreditLedger(_auth).GrantAsync(household.Id, 5_000_000, "seed");

        // No household or subscription id — a refund carries the customer; resolve by it. The refund
        // exceeds the balance, so it goes negative (§4: nets against future purchases).
        var evt = new PaymentWebhookEvent("evt_refund", PaymentEventKind.Refunded,
            BillingCustomerId: "cus_r", AmountMicros: 10_000_000);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        Assert.Equal(-5_000_000, await BalanceAsync(household.Id));
    }

    [Fact]
    public async Task A_payment_failure_changes_no_tier()
    {
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.SubscriptionId = "sub_f";
        });
        var evt = new PaymentWebhookEvent("evt_fail", PaymentEventKind.PaymentFailed, SubscriptionId: "sub_f");

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        Assert.Equal(HouseholdTier.Aware, (await ReloadAsync(household.Id)).Tier); // dunning; a terminal fail arrives as Cancelled
    }

    [Fact]
    public async Task A_duplicate_event_is_applied_only_once()
    {
        var household = await SeedAsync(h => h.Tier = HouseholdTier.Aware);
        var evt = new PaymentWebhookEvent("evt_dup", PaymentEventKind.CheckoutCompleted,
            HouseholdId: household.Id, Product: BillingProduct.CreditPack10, AmountMicros: 10_000_000);

        Assert.Equal(WebhookOutcome.Applied, await Handler().HandleAsync(evt));
        Assert.Equal(WebhookOutcome.AlreadyProcessed, await Handler().HandleAsync(evt)); // same id again

        Assert.Equal(10_000_000, await BalanceAsync(household.Id)); // granted once, not twice
    }

    [Fact]
    public async Task An_event_for_an_unknown_household_is_acked_and_recorded_not_applied()
    {
        var evt = new PaymentWebhookEvent("evt_ghost", PaymentEventKind.CheckoutCompleted,
            HouseholdId: "no-such-household", Product: BillingProduct.SubscriptionMonthly);

        // Acked (a 2xx so the provider stops retrying an unhandleable event)…
        Assert.Equal(WebhookOutcome.UnknownHousehold, await Handler().HandleAsync(evt));
        // …and recorded, so a redelivery is recognised rather than re-attempted.
        Assert.Equal(WebhookOutcome.AlreadyProcessed, await Handler().HandleAsync(evt));
    }

    [Fact]
    public async Task A_lifecycle_event_for_a_superseded_subscription_is_ignored()
    {
        // After a cancel-then-resubscribe (or the purchaser-departure supersede) the household's CURRENT sub
        // is sub_new on the same customer. A late/reordered cancel of the OLD sub still resolves here by the
        // shared customer id, but must NOT clobber the active subscription.
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.BillingCustomerId = "cus_shared";
            h.SubscriptionId = "sub_new";
            h.SubscriptionRenewsAt = PeriodEnd;
        });
        var evt = new PaymentWebhookEvent("evt_stale", PaymentEventKind.SubscriptionCancelled,
            BillingCustomerId: "cus_shared", SubscriptionId: "sub_old"); // resolves by customer, not sub id

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Ignored, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal(HouseholdTier.Aware, after.Tier);        // the active subscription is untouched…
        Assert.Equal("sub_new", after.SubscriptionId);        // …not dropped to Free by the old sub's event
        // …and it's recorded, so a redelivery is recognised rather than re-evaluated.
        Assert.Equal(WebhookOutcome.AlreadyProcessed, await Handler().HandleAsync(evt));
    }

    [Fact]
    public async Task A_new_subscription_supersedes_and_cancels_the_old_one()
    {
        // The purchaser-departure edge (§6): a member re-attaches billing with a fresh subscription, so the
        // old one (on the previous purchaser's card) must be cancelled or the household is billed twice.
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.BillingCustomerId = "cus_old";
            h.SubscriptionId = "sub_old";
            h.SubscriptionRenewsAt = PeriodEnd;
        });
        var evt = new PaymentWebhookEvent("evt_resub", PaymentEventKind.CheckoutCompleted,
            HouseholdId: household.Id, BillingCustomerId: "cus_new", SubscriptionId: "sub_new",
            Product: BillingProduct.SubscriptionMonthly, PeriodEnd: PeriodEnd);

        var outcome = await Handler().HandleAsync(evt);

        Assert.Equal(WebhookOutcome.Applied, outcome);
        var after = await ReloadAsync(household.Id);
        Assert.Equal("sub_new", after.SubscriptionId);   // the new subscription is active…
        Assert.Equal("cus_new", after.BillingCustomerId);
        Assert.Contains("sub_old", _provider.Cancelled);  // …and the old one was cancelled — no double-billing
    }

    [Fact]
    public async Task A_first_subscription_cancels_nothing()
    {
        var household = await SeedAsync(); // Free, no prior subscription
        var evt = new PaymentWebhookEvent("evt_first", PaymentEventKind.CheckoutCompleted,
            HouseholdId: household.Id, SubscriptionId: "sub_1",
            Product: BillingProduct.SubscriptionMonthly, PeriodEnd: PeriodEnd);

        await Handler().HandleAsync(evt);

        Assert.Empty(_provider.Cancelled); // nothing to supersede
    }

    [Fact]
    public async Task A_renewal_does_not_cancel_the_subscription()
    {
        // A renewal reuses the SAME subscription id — it must never be mistaken for a supersede.
        var household = await SeedAsync(h =>
        {
            h.Tier = HouseholdTier.Aware;
            h.SubscriptionId = "sub_1";
        });
        var evt = new PaymentWebhookEvent("evt_renew", PaymentEventKind.SubscriptionRenewed,
            SubscriptionId: "sub_1", PeriodEnd: PeriodEnd);

        await Handler().HandleAsync(evt);

        Assert.Empty(_provider.Cancelled);
    }
}
