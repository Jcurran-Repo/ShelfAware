using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Auth;

/// <summary>
/// A record that a provider webhook event has ALREADY been applied — the idempotency ledger for payments
/// (docs/subscription-plan.md §6: "idempotent by event id"). A merchant of record retries a webhook until
/// it gets a 2xx, so the same event can arrive several times; <see cref="Billing.PaymentWebhookHandler"/>
/// applies each effect exactly once by inserting this row IN THE SAME TRANSACTION as the effect and
/// refusing to re-apply an id it has already seen.
///
/// Lives in auth.db beside the subscription + ledger it guards (all payment state is auth-side). auth.db
/// has no tenancy query filter, so this is looked up by its own unique event id — the id IS the key, not a
/// household scope. Operator/payment data, never household-owned: not exported and not touched by "delete
/// my data" (a wipe of a household's pantry must not let a retried webhook re-apply).
/// </summary>
public sealed class ProcessedPaymentEvent
{
    /// <summary>The provider's own event id — the idempotency key, and the primary key, so a concurrent
    /// duplicate delivery loses the insert race and is recognised as already-processed.</summary>
    public string EventId { get; set; } = "";

    /// <summary>What kind of event it was — for auditing the payment log; not load-bearing (the id alone
    /// decides idempotency).</summary>
    public PaymentEventKind Kind { get; set; }

    /// <summary>The household the event applied to, when it resolved to one — audit only.</summary>
    public string? HouseholdId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.Now;
}
