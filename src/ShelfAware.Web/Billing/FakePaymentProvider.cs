using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Billing;

/// <summary>
/// The deterministic, in-process <see cref="IPaymentProvider"/> — no external calls, no real account —
/// used for local dev and tests, and the only adapter wired until the Stripe Managed Payments adapter
/// lands (phase-3 step 5, docs/subscription-plan.md §6). It is a real fake, not a stub: checkout/portal
/// return deterministic URLs, and <see cref="ParseWebhook"/> does REAL HMAC-SHA256 verification over the
/// raw body (the exact model §6 mandates and every MoR uses), so the webhook endpoint built on it in
/// step 2 can be tested end to end without a payment account. The static <see cref="Serialize"/> +
/// <see cref="Sign"/> pair is the fake's wire format, used by those tests to produce a validly-signed
/// event.
/// </summary>
public sealed class FakePaymentProvider(IOptions<PaymentsOptions> options) : IPaymentProvider
{
    private readonly PaymentsOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public PaymentProviderKind Kind => PaymentProviderKind.Fake;

    /// <summary>The header the fake's signature rides in — what the webhook endpoint reads and passes to
    /// <see cref="ParseWebhook"/>, and what a test (or a dev tool simulating a callback) sets.</summary>
    public string SignatureHeaderName => "X-Fake-Signature";

    /// <summary>A deterministic checkout URL carrying the request, so a test can assert what was asked and
    /// a later dev-only page could honour it to simulate a completed purchase. The fake has no hosted page
    /// of its own — in the real adapter this is the provider's hosted checkout URL.</summary>
    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var url =
            $"/billing/fake-checkout?product={request.Product}" +
            $"&household={Uri.EscapeDataString(request.HouseholdId)}" +
            $"&email={Uri.EscapeDataString(request.PurchaserEmail)}" +
            $"&success={Uri.EscapeDataString(request.SuccessUrl)}" +
            $"&cancel={Uri.EscapeDataString(request.CancelUrl)}";
        return Task.FromResult(new CheckoutSession(url));
    }

    /// <summary>A deterministic portal URL — in the real adapter, the provider's hosted customer portal.</summary>
    public Task<string> CreatePortalUrlAsync(string billingCustomerId, string returnUrl, CancellationToken cancellationToken = default)
    {
        var url =
            $"/billing/fake-portal?customer={Uri.EscapeDataString(billingCustomerId)}" +
            $"&return={Uri.EscapeDataString(returnUrl)}";
        return Task.FromResult(url);
    }

    /// <summary>Verify the raw body against its signature and parse it, or null on a bad/missing signature
    /// or malformed body. Constant-time comparison, exactly as the real endpoint must do (§6).</summary>
    public PaymentWebhookEvent? ParseWebhook(string payload, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signatureHeader)) return null;
        // No configured secret means nothing can be verified — reject, never fall back to a hard-coded
        // key (a known secret in a public repo would be a forgery trapdoor). A box that ENABLES payments
        // is required to set Payments:WebhookSigningSecret (Program.cs ValidateOnStart), so this null path
        // is only reachable for a disabled/programmatically-constructed provider.
        var secret = _options.WebhookSigningSecret;
        if (string.IsNullOrEmpty(secret)) return null;
        if (!SignaturesMatch(Sign(secret, payload), signatureHeader)) return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<PaymentWebhookEvent>(payload, JsonOptions);
            // A signed-but-empty event (no id to dedupe on) is malformed — reject rather than pass a
            // half-event to the idempotent handler.
            if (parsed is null || string.IsNullOrEmpty(parsed.EventId)) return null;
            // JsonStringEnumConverter also accepts NUMERIC enum values, so a validly-signed body can smuggle
            // an out-of-range Kind/Product past deserialization (CLAUDE.md item 38 — the numeric-smuggling
            // hazard this repo guards at every enum parse). Reject an undefined value rather than hand the
            // handler a (PaymentEventKind)42 to switch on.
            if (!Enum.IsDefined(parsed.Kind)) return null;
            if (parsed.Product is { } product && !Enum.IsDefined(product)) return null;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The fake has no backing store to cancel — a real cancellation's effect arrives as a
    /// webhook, which tests simulate via <see cref="Serialize"/> + <see cref="Sign"/> + <see cref="ParseWebhook"/>.
    /// Completes so the caller's flow (step 4) can be exercised.</summary>
    public Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>The fake's wire format for a webhook body — used with <see cref="Sign"/> to produce a
    /// validly-signed event in tests (and by any dev affordance that simulates a provider callback).</summary>
    public static string Serialize(PaymentWebhookEvent webhookEvent) => JsonSerializer.Serialize(webhookEvent, JsonOptions);

    /// <summary>HMAC-SHA256 of the raw payload under the secret, lowercase hex — the signature the endpoint
    /// verifies. Static so a test can sign a payload the same way the sender would.</summary>
    public static string Sign(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool SignaturesMatch(string expectedHex, string providedHex)
    {
        byte[] expected, provided;
        try
        {
            expected = Convert.FromHexString(expectedHex);
            provided = Convert.FromHexString(providedHex.Trim());
        }
        catch (FormatException)
        {
            return false; // a non-hex signature can't match — and can't be decoded to compare
        }

        // FixedTimeEquals is constant-time and safe on a length mismatch — don't short-circuit on Length.
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
