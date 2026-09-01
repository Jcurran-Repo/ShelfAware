using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Billing;

/// <summary>What handling a verified webhook did — what the endpoint turns into a (2xx) response. Every
/// outcome is a 2xx: the provider must stop retrying once we've taken responsibility for the event, whether
/// we applied it, had already applied it, or acked an unhandleable one. A real failure throws instead (the
/// endpoint answers 5xx so the provider retries).</summary>
public enum WebhookOutcome
{
    /// <summary>Effects applied and the event recorded.</summary>
    Applied,

    /// <summary>The event id was already recorded (a provider retry, or a concurrent duplicate that raced
    /// us) — a no-op, so nothing is applied twice.</summary>
    AlreadyProcessed,

    /// <summary>Authentic (signature verified) but it named no household we know — recorded so it won't
    /// loop, logged for the operator, and acked. Not an error to retry.</summary>
    UnknownHousehold,
}

/// <summary>
/// Applies a VERIFIED payment webhook event to the household's subscription + credit state (phase 3 step 2,
/// docs/subscription-plan.md §6). The endpoint owns signature verification (via <see cref="IPaymentProvider.ParseWebhook"/>);
/// this owns the effects, idempotently.
///
/// Everything lands in ONE auth.db transaction: the tier/period change on <see cref="Household"/>, any
/// ledger entries (pack → grant, refund → reversal), AND the <see cref="ProcessedPaymentEvent"/> row that
/// makes it idempotent. So a retried event can't double-apply (the id is already recorded), and a failure
/// applies nothing (the transaction rolls back, the provider retries). auth.db has no tenancy query filter,
/// so the household is resolved by the event's own verified identifiers — never a request-supplied scope.
/// </summary>
public sealed class PaymentWebhookHandler(
    IDbContextFactory<AuthDbContext> authDb,
    IPaymentProvider provider,
    ILogger<PaymentWebhookHandler> logger)
{
    public async Task<WebhookOutcome> HandleAsync(PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken = default)
    {
        await using var db = await authDb.CreateDbContextAsync(cancellationToken);

        // Idempotency fast path: a retry of an event we've already applied.
        if (await db.ProcessedPaymentEvents.AnyAsync(e => e.EventId == webhookEvent.EventId, cancellationToken))
            return WebhookOutcome.AlreadyProcessed;

        var household = await ResolveHouseholdAsync(db, webhookEvent, cancellationToken);
        if (household is null)
        {
            logger.LogError(
                "Payment webhook {EventId} ({Kind}) matched no household (household={HouseholdId}, sub={SubscriptionId}, customer={CustomerId}).",
                webhookEvent.EventId, webhookEvent.Kind, webhookEvent.HouseholdId, webhookEvent.SubscriptionId, webhookEvent.BillingCustomerId);
            db.ProcessedPaymentEvents.Add(Record(webhookEvent, householdId: null));
            return await SaveDedupedAsync(db, webhookEvent, cancellationToken)
                ? WebhookOutcome.UnknownHousehold
                : WebhookOutcome.AlreadyProcessed;
        }

        // A new subscription checkout SUPERSEDES an existing one (§6, the purchaser-departure edge): the
        // old sub is on the previous purchaser's card, so once a member re-attaches billing with a fresh
        // subscription, the old one must be cancelled or the household is billed twice. Captured before
        // Apply overwrites SubscriptionId; cancelled AFTER the commit (an external API call, not part of
        // the DB transaction). A renewal reuses the same id, so it never supersedes.
        var supersededSubscriptionId =
            webhookEvent.Kind == PaymentEventKind.CheckoutCompleted
            && webhookEvent.Product is { } product && BillingCatalog.IsSubscription(product)
            && !string.IsNullOrEmpty(household.SubscriptionId)
            && household.SubscriptionId != webhookEvent.SubscriptionId
                ? household.SubscriptionId
                : null;

        Apply(db, household, webhookEvent);
        db.ProcessedPaymentEvents.Add(Record(webhookEvent, household.Id));
        if (!await SaveDedupedAsync(db, webhookEvent, cancellationToken))
            return WebhookOutcome.AlreadyProcessed;

        if (supersededSubscriptionId is not null)
            await CancelSupersededAsync(supersededSubscriptionId, cancellationToken);

        return WebhookOutcome.Applied;
    }

    /// <summary>Cancel a subscription a new checkout replaced — best-effort, AFTER the commit. A failure is
    /// logged, not fatal: the new subscription is already active, and the old one can be cancelled from the
    /// provider dashboard. Never lets a provider hiccup fail an event we've already applied.</summary>
    private async Task CancelSupersededAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await provider.CancelSubscriptionAsync(subscriptionId, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Couldn't cancel superseded subscription {SubscriptionId} — cancel it in the provider dashboard to stop double-billing.",
                subscriptionId);
        }
    }

    /// <summary>Resolve the target household from the event's OWN verified identifiers, in order of
    /// specificity: the household-id metadata a checkout carries, then the subscription id, then the
    /// customer id (both stored at checkout, so a later renewal/cancel that carries only those still
    /// finds its household).</summary>
    private static async Task<Household?> ResolveHouseholdAsync(
        AuthDbContext db, PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(webhookEvent.HouseholdId))
        {
            var byId = await db.Households.FirstOrDefaultAsync(h => h.Id == webhookEvent.HouseholdId, cancellationToken);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrEmpty(webhookEvent.SubscriptionId))
        {
            var bySub = await db.Households.FirstOrDefaultAsync(h => h.SubscriptionId == webhookEvent.SubscriptionId, cancellationToken);
            if (bySub is not null) return bySub;
        }
        if (!string.IsNullOrEmpty(webhookEvent.BillingCustomerId))
        {
            var byCustomer = await db.Households.FirstOrDefaultAsync(h => h.BillingCustomerId == webhookEvent.BillingCustomerId, cancellationToken);
            if (byCustomer is not null) return byCustomer;
        }
        return null;
    }

    private void Apply(AuthDbContext db, Household household, PaymentWebhookEvent webhookEvent)
    {
        switch (webhookEvent.Kind)
        {
            case PaymentEventKind.CheckoutCompleted:
                if (webhookEvent.Product is { } subProduct && BillingCatalog.IsSubscription(subProduct))
                {
                    ActivateSubscription(household, webhookEvent);
                }
                else if (webhookEvent.Product is { } packProduct && BillingCatalog.IsPack(packProduct))
                {
                    // A pack buyer already has the customer id from subscribing (packs are subscribers-only,
                    // §8), but keep whatever the event carries if we somehow don't.
                    household.BillingCustomerId ??= webhookEvent.BillingCustomerId;
                    var entry = CreditLedger.Purchase(household.Id, webhookEvent.AmountMicros ?? 0, "Credit pack");
                    if (entry is not null) db.CreditLedger.Add(entry);
                }
                else
                {
                    logger.LogWarning("Payment webhook {EventId} is a checkout with no product — nothing to apply.", webhookEvent.EventId);
                }
                break;

            case PaymentEventKind.SubscriptionRenewed:
                ActivateSubscription(household, webhookEvent); // still active, for a new period
                break;

            case PaymentEventKind.SubscriptionUpdated:
                // A state change without a renewal — most often cancel-at-period-end toggled. The tier
                // stays (Aware until the period actually ends); update the period + the cancel flag.
                if (webhookEvent.PeriodEnd is not null) household.SubscriptionRenewsAt = webhookEvent.PeriodEnd;
                household.SubscriptionCancelAtPeriodEnd = webhookEvent.CancelAtPeriodEnd;
                break;

            case PaymentEventKind.SubscriptionCancelled:
                // The subscription has ended. Tier drops to Free — a POSTURE, so nothing is deleted (§6),
                // and purchased credits survive (the ledger is untouched). The customer id is kept so the
                // household can re-subscribe or reach the portal.
                household.Tier = HouseholdTier.Free;
                household.SubscriptionId = null;
                household.SubscriptionRenewsAt = null;
                household.SubscriptionCancelAtPeriodEnd = false;
                break;

            case PaymentEventKind.PaymentFailed:
                // Dunning has begun; the provider retries the charge. No tier change here — the terminal
                // failure arrives as SubscriptionCancelled. The ProcessedPaymentEvent row is the audit trail.
                break;

            case PaymentEventKind.Refunded:
                var reversal = CreditLedger.Refund(household.Id, webhookEvent.AmountMicros ?? 0, "Refund");
                if (reversal is not null) db.CreditLedger.Add(reversal); // balance may go negative (§4)
                break;

            default:
                // A kind the parse admitted (Enum.IsDefined) but this handler has no arm for — future-proofing.
                // Recorded + acked (no retry storm) but visible in the log so a new kind gets a handler.
                logger.LogWarning("Payment webhook {EventId} has unhandled kind {Kind} — recorded, no effect.", webhookEvent.EventId, webhookEvent.Kind);
                break;
        }
    }

    private static void ActivateSubscription(Household household, PaymentWebhookEvent webhookEvent)
    {
        household.Tier = HouseholdTier.Aware;
        if (webhookEvent.BillingCustomerId is not null) household.BillingCustomerId = webhookEvent.BillingCustomerId;
        if (webhookEvent.SubscriptionId is not null) household.SubscriptionId = webhookEvent.SubscriptionId;
        if (webhookEvent.PeriodEnd is not null) household.SubscriptionRenewsAt = webhookEvent.PeriodEnd;
        household.SubscriptionCancelAtPeriodEnd = webhookEvent.CancelAtPeriodEnd;
    }

    private static ProcessedPaymentEvent Record(PaymentWebhookEvent webhookEvent, string? householdId) => new()
    {
        EventId = webhookEvent.EventId,
        Kind = webhookEvent.Kind,
        HouseholdId = householdId,
    };

    /// <summary>Save the transaction, distinguishing a concurrent-duplicate insert race (the event id's
    /// unique PK trips → the racer already applied it, so this is a no-op → false) from a real DB failure
    /// (rethrow → the endpoint answers 5xx and the provider retries). The re-check runs on a FRESH context
    /// because the failed one's change tracker is no longer trustworthy.</summary>
    private async Task<bool> SaveDedupedAsync(AuthDbContext db, PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await using var check = await authDb.CreateDbContextAsync(cancellationToken);
            if (await check.ProcessedPaymentEvents.AnyAsync(e => e.EventId == webhookEvent.EventId, cancellationToken))
                return false; // a concurrent duplicate delivery won the insert race
            throw; // a different failure — let the provider retry
        }
    }
}
