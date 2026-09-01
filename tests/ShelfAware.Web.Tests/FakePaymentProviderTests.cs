using Microsoft.Extensions.Options;
using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The in-process payment adapter (phase 3 step 1). The crux is <see cref="FakePaymentProvider.ParseWebhook"/>:
/// it does the same raw-body HMAC verification the real endpoint must (docs/subscription-plan.md §6), so the
/// webhook handler built on it in step 2 can be tested without a payment account. Checkout/portal are
/// asserted for determinism + content; cancel simply completes.
/// </summary>
public class FakePaymentProviderTests
{
    private const string Secret = "whsec_test_1234";

    private static FakePaymentProvider Provider(string? secret = Secret) =>
        new(Options.Create(new PaymentsOptions { WebhookSigningSecret = secret }));

    private static PaymentWebhookEvent SampleEvent() => new(
        EventId: "evt_1",
        Kind: PaymentEventKind.CheckoutCompleted,
        HouseholdId: "hh_1",
        BillingCustomerId: "cus_1",
        SubscriptionId: "sub_1",
        Product: BillingProduct.SubscriptionMonthly,
        PeriodEnd: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        CancelAtPeriodEnd: false,
        AmountMicros: null);

    /// <summary>A rejection is InvalidSignature with no event — the fake never returns the
    /// verified-but-ignore outcome (that's a real-provider firehose concern).</summary>
    private static void AssertRejected(WebhookParse parse)
    {
        Assert.Equal(WebhookParseResult.InvalidSignature, parse.Result);
        Assert.Null(parse.Event);
    }

    [Fact]
    public void Kind_is_fake() => Assert.Equal(PaymentProviderKind.Fake, Provider().Kind);

    [Fact]
    public async Task Checkout_url_is_deterministic_and_carries_the_request()
    {
        var request = new CheckoutRequest("hh 1", "a@b.com", BillingProduct.SubscriptionAnnual, "/ok", "/no");
        var first = await Provider().CreateCheckoutAsync(request);
        var second = await Provider().CreateCheckoutAsync(request);

        Assert.Equal(first.Url, second.Url); // deterministic — same request, same URL
        Assert.Contains("product=SubscriptionAnnual", first.Url);
        Assert.Contains("household=hh%201", first.Url); // the space is escaped, not passed raw
        Assert.Contains("email=a%40b.com", first.Url);
    }

    [Fact]
    public async Task Portal_url_carries_the_customer_and_return()
    {
        var url = await Provider().CreatePortalUrlAsync("cus_9", "/settings");
        Assert.Contains("customer=cus_9", url);
        Assert.Contains("return=%2Fsettings", url);
    }

    [Fact]
    public void A_correctly_signed_webhook_parses_to_the_original_event()
    {
        var evt = SampleEvent();
        var payload = FakePaymentProvider.Serialize(evt);
        var signature = FakePaymentProvider.Sign(Secret, payload);

        var parse = Provider().ParseWebhook(payload, signature);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        Assert.Equal(evt, parse.Event); // record value-equality pins every field through the JSON round-trip
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        var payload = FakePaymentProvider.Serialize(SampleEvent());
        var signature = FakePaymentProvider.Sign(Secret, payload);

        var tampered = payload.Replace("hh_1", "hh_2"); // same signature, different body
        AssertRejected(Provider().ParseWebhook(tampered, signature));
    }

    [Fact]
    public void A_signature_from_the_wrong_secret_is_rejected()
    {
        var payload = FakePaymentProvider.Serialize(SampleEvent());
        var wrong = FakePaymentProvider.Sign("some-other-secret", payload);

        AssertRejected(Provider().ParseWebhook(payload, wrong));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex-zzzz")]
    public void A_missing_or_malformed_signature_is_rejected(string? signature)
    {
        var payload = FakePaymentProvider.Serialize(SampleEvent());
        AssertRejected(Provider().ParseWebhook(payload, signature));
    }

    [Fact]
    public void A_validly_signed_but_unparseable_body_is_rejected()
    {
        const string payload = "{not valid json";
        var signature = FakePaymentProvider.Sign(Secret, payload); // signature is fine; the body isn't
        AssertRejected(Provider().ParseWebhook(payload, signature));
    }

    [Fact]
    public void A_validly_signed_event_with_no_id_is_rejected()
    {
        // An event with no id can't be deduped by the idempotent handler — reject it rather than pass a
        // half-event through, even though its signature is valid.
        var payload = FakePaymentProvider.Serialize(SampleEvent() with { EventId = "" });
        var signature = FakePaymentProvider.Sign(Secret, payload);
        AssertRejected(Provider().ParseWebhook(payload, signature));
    }

    [Fact]
    public void A_webhook_is_rejected_when_no_secret_is_configured()
    {
        // With no configured secret the fake can't verify anything, so it rejects rather than fall back to
        // a hard-coded key (a known secret in a public repo would be a forgery trapdoor). A deployed box
        // that enables payments is required to set the secret (Program.cs ValidateOnStart).
        var provider = Provider(secret: null);
        var payload = FakePaymentProvider.Serialize(SampleEvent());
        var signature = FakePaymentProvider.Sign("anything", payload);
        AssertRejected(provider.ParseWebhook(payload, signature));
    }

    [Fact]
    public void A_validly_signed_event_with_an_out_of_range_kind_is_rejected()
    {
        // JsonStringEnumConverter accepts NUMERIC enum values, so a valid signature can smuggle an
        // undefined Kind past deserialization — reject it rather than hand the handler a
        // (PaymentEventKind)42 to switch on (CLAUDE.md item 38, the numeric-smuggling hazard).
        const string payload = """{"EventId":"evt_1","Kind":42}""";
        var signature = FakePaymentProvider.Sign(Secret, payload);
        AssertRejected(Provider().ParseWebhook(payload, signature));
    }

    [Fact]
    public void A_validly_signed_event_with_an_out_of_range_product_is_rejected()
    {
        const string payload = """{"EventId":"evt_1","Kind":"CheckoutCompleted","Product":99}""";
        var signature = FakePaymentProvider.Sign(Secret, payload);
        AssertRejected(Provider().ParseWebhook(payload, signature));
    }

    [Fact]
    public async Task Cancel_completes() => await Provider().CancelSubscriptionAsync("sub_1");
}
