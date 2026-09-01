using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace ShelfAware.Web.Billing;

/// <summary>
/// The real merchant-of-record adapter (phase-3 step 5, docs/subscription-plan.md §6): Stripe Managed
/// Payments over the Stripe.net SDK. Every Checkout Session sets <c>managed_payments[enabled]=true</c>, so
/// Stripe is the merchant of record — it collects and remits sales tax, and owns disputes/receipts — and the
/// app never sets tax/shipping/Connect/payment-method params (Managed Payments forbids them, and we simply
/// don't send them).
///
/// Checkout and portal are HOSTED-REDIRECT (the strict CSP forbids a JS overlay): each returns the provider's
/// hosted URL for the endpoint to 302 to. <see cref="ParseWebhook"/> verifies Stripe's signature
/// (<c>t=..,v1=hmac</c> over <c>"&lt;timestamp&gt;.&lt;rawBody&gt;"</c> — NOT the fake's plain hex) via
/// <see cref="EventUtility.ConstructEvent(string, string, string, long, bool)"/>, then maps the handful of
/// Stripe event types the household lifecycle needs; every other (verified) type is acked and ignored.
///
/// The <see cref="IStripeClient"/> is injected so tests drive a fake HTTP backend (asserting the outgoing
/// params, e.g. that managed_payments really is set) without a live account or network.
/// </summary>
public sealed class StripePaymentProvider(
    IStripeClient client,
    IOptions<PaymentsOptions> options,
    ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    private readonly PaymentsOptions _options = options.Value;

    public PaymentProviderKind Kind => PaymentProviderKind.StripeManagedPayments;

    /// <summary>Stripe puts each webhook's signature in this header — the value <see cref="EventUtility"/>
    /// verifies against the endpoint's signing secret.</summary>
    public string SignatureHeaderName => "Stripe-Signature";

    // Metadata keys carried on the Checkout SESSION so the completion webhook maps back to the household +
    // product with no price-id reverse lookup. Deliberately NOT set on the subscription: keeping household
    // identity off the subscription is what lets a superseded/old subscription's later events fail to resolve
    // back to a household that has since moved to a new subscription (PaymentWebhookHandler resolves those by
    // subscription id, and its stale-subscription guard is the backstop).
    private const string HouseholdMetadataKey = "household_id";
    private const string ProductMetadataKey = "product";

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var priceId = _options.PriceIdFor(request.Product)
            ?? throw new InvalidOperationException($"No Stripe price id is configured for {request.Product}.");
        var isSubscription = BillingCatalog.IsSubscription(request.Product);

        var sessionOptions = new SessionCreateOptions
        {
            Mode = isSubscription ? "subscription" : "payment",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            Metadata = new Dictionary<string, string>
            {
                [HouseholdMetadataKey] = request.HouseholdId,
                [ProductMetadataKey] = request.Product.ToString(),
            },
        };

        // A pack is bought by an existing subscriber → attach it to their customer (tidy dashboard, one
        // customer per household). A subscription always uses the purchaser's verified email so a NEW member
        // (the purchaser-departure supersede) gets their own customer rather than capturing the old one's.
        if (!isSubscription && !string.IsNullOrEmpty(request.ExistingCustomerId))
            sessionOptions.Customer = request.ExistingCustomerId;
        else
            sessionOptions.CustomerEmail = request.PurchaserEmail;

        // Merchant of record. Set via the raw param so the flag is correct regardless of the SDK's typed
        // surface for this new-ish API field (Dahlia 2026-04-22), and so it self-documents that this session
        // is MoR even if the account default is also on.
        sessionOptions.AddExtraParam("managed_payments[enabled]", "true");

        var session = await new SessionService(client).CreateAsync(sessionOptions, cancellationToken: cancellationToken);
        return new CheckoutSession(session.Url);
    }

    public async Task<string> CreatePortalUrlAsync(string billingCustomerId, string returnUrl, CancellationToken cancellationToken = default)
    {
        var session = await new Stripe.BillingPortal.SessionService(client).CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions { Customer = billingCustomerId, ReturnUrl = returnUrl },
            cancellationToken: cancellationToken);
        return session.Url;
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        // Run out the already-paid period rather than cutting off mid-cycle (the interface contract). The
        // effect — the cancel flag now, then the end-of-period termination — arrives back as webhooks, which
        // are what actually update the household. Used by the supersede (purchaser-departure) path.
        await new SubscriptionService(client).UpdateAsync(
            subscriptionId,
            new SubscriptionUpdateOptions { CancelAtPeriodEnd = true },
            cancellationToken: cancellationToken);
    }

    public WebhookParse ParseWebhook(string payload, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signatureHeader))
            return WebhookParse.Invalid;

        Event stripeEvent;
        try
        {
            // Verifies the t=..,v1=.. signature over "<timestamp>.<rawBody>" against the endpoint's signing
            // secret AND deserializes. A forged/absent/expired signature throws before any effect.
            // throwOnApiVersionMismatch:false — the account's default API version may not exactly match this
            // SDK's pinned one, and a mismatch must not fail every otherwise-valid webhook. A validly-signed
            // body is a real Stripe event with valid JSON, so a signature pass implies a parseable body — the
            // one surface to guard is the verification failure.
            stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _options.WebhookSigningSecret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            return WebhookParse.Invalid;
        }

        var mapped = Map(stripeEvent);
        return mapped is null ? WebhookParse.Ignored : WebhookParse.Handle(mapped);
    }

    /// <summary>Map a verified Stripe event to the app's event, or null for a type the app doesn't act on
    /// (acked + ignored). Only the handful of types the household lifecycle needs are mapped; Stripe's
    /// firehose of everything else verifies fine and is deliberately dropped here.</summary>
    private PaymentWebhookEvent? Map(Event stripeEvent) => stripeEvent.Type switch
    {
        "checkout.session.completed" => MapCheckout(stripeEvent),
        // created + updated both carry the real items[].current_period_end and the cancel flag; the handler
        // applies them idempotently and only to the household's CURRENT subscription (a stale/superseded one
        // is ignored there). Renewals arrive as updated (the period advances), so no separate renewal map.
        "customer.subscription.created" or "customer.subscription.updated" => MapSubscriptionChange(stripeEvent),
        "customer.subscription.deleted" => MapSubscriptionDeleted(stripeEvent),
        "invoice.payment_failed" => MapPaymentFailed(stripeEvent),
        // Everything else (payment_intent.*, charge.*, customer.*, and — for now — charge.refunded, whose
        // tax-and-product-aware credit clawback is deferred to a later step) verifies but is not acted on.
        _ => null,
    };

    private PaymentWebhookEvent? MapCheckout(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Session session) return Unmapped(stripeEvent, "event data was not a checkout session");
        // Only a fully completed session grants anything (an "open"/"expired" one hasn't been paid).
        if (session.Status != "complete") return null;

        var product = ParseProduct(session.Metadata);
        if (product is null) return Unmapped(stripeEvent, "unknown or missing product metadata");

        var isPack = BillingCatalog.IsPack(product.Value);
        // A pack grants its FACE value (not session.AmountTotal, which under MoR includes tax). A subscription
        // grants no credit here (the fee isn't a credit; the monthly allowance is a later step).
        long? amount = isPack ? BillingCatalog.RetailMicrosFor(product.Value) : null;
        // The session doesn't carry the subscription's period end; provision one that the subscription.created/
        // updated event corrects with the real anchor. A pack has no period.
        DateTimeOffset? periodEnd = isPack
            ? null
            : DateTimeOffset.UtcNow.AddDays(product.Value == BillingProduct.SubscriptionAnnual ? 365 : 30);

        return new PaymentWebhookEvent(
            EventId: stripeEvent.Id,
            Kind: PaymentEventKind.CheckoutCompleted,
            HouseholdId: Meta(session.Metadata, HouseholdMetadataKey),
            BillingCustomerId: session.CustomerId,
            SubscriptionId: session.SubscriptionId,
            Product: product,
            PeriodEnd: periodEnd,
            CancelAtPeriodEnd: false,
            AmountMicros: amount);
    }

    private PaymentWebhookEvent? MapSubscriptionChange(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Subscription sub) return Unmapped(stripeEvent, "event data was not a subscription");
        return new PaymentWebhookEvent(
            EventId: stripeEvent.Id,
            Kind: PaymentEventKind.SubscriptionUpdated,
            BillingCustomerId: sub.CustomerId,
            SubscriptionId: sub.Id,
            PeriodEnd: CurrentPeriodEnd(sub),
            CancelAtPeriodEnd: sub.CancelAtPeriodEnd);
    }

    private PaymentWebhookEvent? MapSubscriptionDeleted(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Subscription sub) return Unmapped(stripeEvent, "event data was not a subscription");
        return new PaymentWebhookEvent(
            EventId: stripeEvent.Id,
            Kind: PaymentEventKind.SubscriptionCancelled,
            BillingCustomerId: sub.CustomerId,
            SubscriptionId: sub.Id);
    }

    private PaymentWebhookEvent? MapPaymentFailed(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Invoice invoice) return Unmapped(stripeEvent, "event data was not an invoice");
        // Audit-only in the handler (dunning has begun; a terminal failure arrives as subscription.deleted).
        // Resolve by customer — enough to attribute a no-effect record; a Basil invoice's subscription id sits
        // under Parent.SubscriptionDetails, which isn't needed for this.
        return new PaymentWebhookEvent(
            EventId: stripeEvent.Id,
            Kind: PaymentEventKind.PaymentFailed,
            BillingCustomerId: invoice.CustomerId);
    }

    /// <summary>Basil (2025-03-31) moved current_period_end off the subscription onto its items — read it from
    /// the (single, one-price) item, taking the latest if a future plan ever carries several. Stripe times are
    /// UTC; build the offset from ticks so a DateTimeKind quirk can't throw.</summary>
    private static DateTimeOffset? CurrentPeriodEnd(Subscription sub)
    {
        if (sub.Items?.Data is not { Count: > 0 } items) return null;
        DateTime? latest = null;
        foreach (var item in items)
            if (latest is null || item.CurrentPeriodEnd > latest.Value) latest = item.CurrentPeriodEnd;
        return latest is { } end ? new DateTimeOffset(end.Ticks, TimeSpan.Zero) : null;
    }

    private static BillingProduct? ParseProduct(IDictionary<string, string>? metadata)
    {
        var raw = Meta(metadata, ProductMetadataKey);
        // Enum.TryParse accepts numeric strings, so guard with IsDefined (numeric-smuggling, CLAUDE.md item 38)
        // — this is metadata we set, but defense in depth costs nothing.
        return Enum.TryParse<BillingProduct>(raw, out var p) && Enum.IsDefined(p) ? p : null;
    }

    private static string? Meta(IDictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private PaymentWebhookEvent? Unmapped(Event stripeEvent, string why)
    {
        logger.LogWarning("Stripe webhook {EventId} ({Type}) couldn't be mapped: {Why}.", stripeEvent.Id, stripeEvent.Type, why);
        return null;
    }
}
