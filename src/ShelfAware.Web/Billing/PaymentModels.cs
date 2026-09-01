namespace ShelfAware.Web.Billing;

/// <summary>What a member can buy through hosted checkout (docs/subscription-plan.md §6): the
/// subscription in either billing period, or a one-time credit pack. The real adapter maps each to a
/// provider price id (<see cref="PaymentsOptions"/>); the fake echoes it back. Credit packs are offered
/// to active subscribers only (§8) — that's a UI/gating rule for step 3, not a shape of this enum.</summary>
public enum BillingProduct
{
    SubscriptionMonthly,
    SubscriptionAnnual,
    CreditPack5,
    CreditPack10,
    CreditPack20,
}

/// <summary>The kinds of provider webhook the app acts on (§6: "checkout completed, subscription
/// renewed/updated/cancelled, refunds"; plus a failed payment for dunning). The webhook handler (step 2)
/// switches on this to update the household's tier + period and write ledger entries. Extensible — a new
/// provider event the app doesn't handle simply parses to a kind it ignores.</summary>
public enum PaymentEventKind
{
    /// <summary>A checkout finished successfully — a new subscription, or a credit-pack purchase.</summary>
    CheckoutCompleted,

    /// <summary>An existing subscription renewed for another period (drop the next monthly grant, extend
    /// the period).</summary>
    SubscriptionRenewed,

    /// <summary>A subscription's state changed without renewing — e.g. cancel-at-period-end toggled, or a
    /// plan/period change.</summary>
    SubscriptionUpdated,

    /// <summary>A subscription ended (period ran out after a cancel, or was terminated). Tier drops to
    /// Free — a posture, so nothing is deleted (§6).</summary>
    SubscriptionCancelled,

    /// <summary>A renewal payment failed — the provider's dunning has begun. Final failure ends the
    /// subscription (arrives as <see cref="SubscriptionCancelled"/>).</summary>
    PaymentFailed,

    /// <summary>A charge was refunded — the ledger posts a reversal entry; a balance may go negative (§4).</summary>
    Refunded,
}

/// <summary>A request to open hosted checkout for one product. <see cref="HouseholdId"/> rides to the
/// provider as metadata so the completion webhook maps back to the right household; the subscription
/// attaches to the household, not the account (§6). <see cref="PurchaserEmail"/> is the member's
/// already-verified account email — §6 keys the provider customer on that one address and never collects a
/// separate billing email (the strict CSP is hosted-redirect-only). Success/cancel URLs are where the
/// provider returns the browser after the hosted page.</summary>
public sealed record CheckoutRequest(
    string HouseholdId,
    string PurchaserEmail,
    BillingProduct Product,
    string SuccessUrl,
    string CancelUrl);

/// <summary>The result of opening checkout: the hosted-redirect URL to send the browser to. A record
/// rather than a bare string so a provider that also returns a correlatable session id can add it here
/// without changing the seam.</summary>
public sealed record CheckoutSession(string Url);

/// <summary>A verified, parsed webhook event — what <see cref="IPaymentProvider.ParseWebhook"/> returns
/// once the signature checks out. <see cref="EventId"/> is the provider's own event id, the idempotency
/// key the handler dedupes on (a provider retries a webhook until it gets a 2xx). The remaining fields are
/// populated per <see cref="Kind"/>: a checkout carries the customer/subscription ids + what was bought;
/// a renewal/update carries the new period + cancel flag; a refund carries the amount. Whatever a given
/// event doesn't speak to stays null/false. Money is retail micros (§4), the ledger's unit.</summary>
public sealed record PaymentWebhookEvent(
    string EventId,
    PaymentEventKind Kind,
    string? HouseholdId = null,
    string? BillingCustomerId = null,
    string? SubscriptionId = null,
    BillingProduct? Product = null,
    DateTimeOffset? PeriodEnd = null,
    bool CancelAtPeriodEnd = false,
    long? AmountMicros = null);
