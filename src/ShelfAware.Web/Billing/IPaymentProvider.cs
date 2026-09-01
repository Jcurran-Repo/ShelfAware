namespace ShelfAware.Web.Billing;

/// <summary>
/// The one thin seam over the merchant-of-record (docs/subscription-plan.md §6: "keep the provider
/// integration behind one thin seam … so the MoR choice stays swappable"). Deliberately NOT a speculative
/// abstraction — it exposes exactly the four operations the plan's mechanics require: open hosted
/// checkout, open the customer portal, parse+verify a webhook, and cancel a subscription. One provider,
/// one adapter: <see cref="FakePaymentProvider"/> for dev/tests today, the Stripe Managed Payments adapter
/// in step 5.
///
/// The strict CSP (<c>form-action 'self'</c>, no third-party scripts) rules out any JS-overlay checkout,
/// so checkout and portal are HOSTED-REDIRECT links only — every method that "starts" a flow returns a URL
/// the app redirects the browser to, never markup or a client token.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Which adapter this is — for diagnostics, so an operator (or a later admin/health surface)
    /// can tell at a glance whether a box is on the fake or the real provider.</summary>
    PaymentProviderKind Kind { get; }

    /// <summary>The HTTP header the provider puts each webhook's signature in, so the endpoint knows which
    /// one to read and hand to <see cref="ParseWebhook"/> (it's provider-specific — Stripe's is
    /// "Stripe-Signature"). Keeps header knowledge with the adapter rather than hard-coded at the endpoint.</summary>
    string SignatureHeaderName { get; }

    /// <summary>Open hosted checkout for one product and return the URL to redirect the browser to. The
    /// household id rides to the provider as metadata so the completion webhook maps back (§6).</summary>
    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Open the customer portal (cancel / card management) for a household's provider customer and
    /// return the URL to redirect to. <paramref name="returnUrl"/> is where the provider sends the browser
    /// back when the member is done.</summary>
    Task<string> CreatePortalUrlAsync(string billingCustomerId, string returnUrl, CancellationToken cancellationToken = default);

    /// <summary>Verify a raw webhook body against its signature header and, if it verifies, map it. Returns a
    /// <see cref="WebhookParse"/> with three outcomes: an unverified signature (400, don't retry), a verified
    /// event to act on, or a verified event of a type this app doesn't handle (2xx, don't retry — a real
    /// provider sends many such events). Takes the EXACT bytes as received (as a string) — the HMAC is over
    /// the raw payload, so the caller must not re-serialize it first (§6). Synchronous: verification + parsing
    /// is pure, no external call.</summary>
    WebhookParse ParseWebhook(string payload, string? signatureHeader);

    /// <summary>Cancel a subscription via the provider API — the purchaser-departure lifecycle and an
    /// explicit member cancel (§6). Cancellation runs out the paid period; the effect arrives back as a
    /// webhook, which is what actually updates the household.</summary>
    Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);
}
