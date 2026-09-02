using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Billing;
using Stripe;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The real Stripe Managed Payments adapter (phase 3 step 5). Two kinds of proof without a live account:
/// checkout/portal/cancel go through a fake HTTP backend so the OUTGOING request is asserted (above all that
/// <c>managed_payments[enabled]=true</c> is set — the merchant-of-record guarantee), and ParseWebhook is
/// driven through the real <see cref="EventUtility"/> signature verification + deserialization so the
/// event→<see cref="PaymentWebhookEvent"/> mapping is pinned end to end. The live subscribe/renew/cancel loop
/// against Stripe's test mode is verified separately.
/// </summary>
public class StripePaymentProviderTests
{
    private const string Secret = "whsec_stripe_test";
    private const string CheckoutResponse = """{"id":"cs_1","object":"checkout.session","url":"https://checkout.stripe.com/pay/cs_1"}""";
    private const string PortalResponse = """{"id":"bps_1","object":"billing_portal.session","url":"https://billing.stripe.com/session/bps_1"}""";
    private const string SubscriptionResponse = """{"id":"sub_1","object":"subscription"}""";

    private static PaymentsOptions MakeOptions(Action<PaymentsOptions>? tweak = null)
    {
        var o = new PaymentsOptions
        {
            Enabled = true,
            Provider = PaymentProviderKind.StripeManagedPayments,
            ApiKey = "sk_test_x",
            WebhookSigningSecret = Secret,
            MonthlyPriceId = "price_monthly",
            AnnualPriceId = "price_annual",
            CreditPack5PriceId = "price_pack5",
            CreditPack10PriceId = "price_pack10",
            CreditPack20PriceId = "price_pack20",
        };
        tweak?.Invoke(o);
        return o;
    }

    /// <summary>A fake HTTP backend for the Stripe SDK: captures the one outgoing request and returns a canned
    /// JSON body. Standard <see cref="HttpMessageHandler"/> (not Stripe's IHttpClient) so it's stable across
    /// SDK versions.</summary>
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string? Content { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }

        /// <summary>The captured request body, %XX-decoded so form keys read as <c>metadata[household_id]</c>
        /// rather than <c>metadata%5Bhousehold_id%5D</c>.</summary>
        public string DecodedContent => System.Uri.UnescapeDataString(Content ?? "");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (StripePaymentProvider provider, CapturingHandler handler) Build(string responseJson, PaymentsOptions? options = null)
    {
        var handler = new CapturingHandler(responseJson);
        var client = new StripeClient("sk_test_x", httpClient: new SystemNetHttpClient(new HttpClient(handler)));
        var provider = new StripePaymentProvider(client, Options.Create(options ?? MakeOptions()), NullLogger<StripePaymentProvider>.Instance);
        return (provider, handler);
    }

    /// <summary>ParseWebhook needs no HTTP — build a provider whose client is never called.</summary>
    private static StripePaymentProvider ParseProvider(PaymentsOptions? options = null) => Build("{}", options).provider;

    /// <summary>Sign a raw body the way Stripe does: <c>t=&lt;ts&gt;,v1=hmac_sha256("&lt;ts&gt;.&lt;body&gt;")</c>.</summary>
    private static string StripeSignature(string payload, string secret = Secret)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{payload}"))).ToLowerInvariant();
        return $"t={ts},v1={hex}";
    }

    private static WebhookParse Parse(string payload) => ParseProvider().ParseWebhook(payload, StripeSignature(payload));

    // ---- Identity ----

    [Fact]
    public void Kind_is_stripe() => Assert.Equal(PaymentProviderKind.StripeManagedPayments, ParseProvider().Kind);

    [Fact]
    public void Signature_header_is_the_stripe_one() => Assert.Equal("Stripe-Signature", ParseProvider().SignatureHeaderName);

    [Theory]
    [InlineData(BillingProduct.SubscriptionMonthly, "price_monthly")]
    [InlineData(BillingProduct.SubscriptionAnnual, "price_annual")]
    [InlineData(BillingProduct.CreditPack5, "price_pack5")]
    [InlineData(BillingProduct.CreditPack10, "price_pack10")]
    [InlineData(BillingProduct.CreditPack20, "price_pack20")]
    public void PriceIdFor_maps_every_product(BillingProduct product, string expected) =>
        Assert.Equal(expected, MakeOptions().PriceIdFor(product));

    // ---- Checkout ----

    [Fact]
    public async Task Subscription_checkout_sets_managed_payments_mode_price_email_and_metadata()
    {
        var (provider, handler) = Build(CheckoutResponse);
        var request = new CheckoutRequest("hh_1", "a@b.com", BillingProduct.SubscriptionMonthly, "https://x/ok", "https://x/no");

        var session = await provider.CreateCheckoutAsync(request);

        Assert.Equal("https://checkout.stripe.com/pay/cs_1", session.Url);
        var body = handler.DecodedContent;
        Assert.Contains("managed_payments[enabled]=true", body); // the merchant-of-record guarantee
        Assert.Contains("mode=subscription", body);
        Assert.Contains("line_items[0][price]=price_monthly", body);
        Assert.Contains("customer_email=a@b.com", body); // a subscription keys on the purchaser's email…
        Assert.DoesNotContain("customer=", body);          // …never an existing customer (supersede-safe)
        Assert.Contains("metadata[household_id]=hh_1", body);
        Assert.Contains("metadata[product]=SubscriptionMonthly", body);
    }

    [Fact]
    public async Task A_pack_checkout_attaches_to_the_existing_customer_in_payment_mode()
    {
        var (provider, handler) = Build(CheckoutResponse);
        var request = new CheckoutRequest("hh_1", "a@b.com", BillingProduct.CreditPack10, "https://x/ok", "https://x/no",
            ExistingCustomerId: "cus_existing");

        await provider.CreateCheckoutAsync(request);

        var body = handler.DecodedContent;
        Assert.Contains("managed_payments[enabled]=true", body);
        Assert.Contains("mode=payment", body);
        Assert.Contains("line_items[0][price]=price_pack10", body);
        Assert.Contains("customer=cus_existing", body);      // the subscriber's customer…
        Assert.DoesNotContain("customer_email=", body);       // …not a fresh one from email
        Assert.Contains("metadata[product]=CreditPack10", body);
    }

    [Fact]
    public async Task A_pack_checkout_with_no_existing_customer_falls_back_to_email()
    {
        var (provider, handler) = Build(CheckoutResponse);
        var request = new CheckoutRequest("hh_1", "a@b.com", BillingProduct.CreditPack5, "https://x/ok", "https://x/no");

        await provider.CreateCheckoutAsync(request);

        Assert.Contains("customer_email=a@b.com", handler.DecodedContent);
    }

    [Fact]
    public async Task Checkout_throws_when_the_price_id_is_not_configured()
    {
        var (provider, _) = Build(CheckoutResponse, MakeOptions(o => o.MonthlyPriceId = null));
        var request = new CheckoutRequest("hh_1", "a@b.com", BillingProduct.SubscriptionMonthly, "https://x/ok", "https://x/no");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateCheckoutAsync(request));
    }

    // ---- Portal + cancel ----

    [Fact]
    public async Task Portal_returns_the_hosted_url_and_carries_customer_and_return()
    {
        var (provider, handler) = Build(PortalResponse);

        var url = await provider.CreatePortalUrlAsync("cus_1", "https://x/settings");

        Assert.Equal("https://billing.stripe.com/session/bps_1", url);
        Assert.Contains("customer=cus_1", handler.DecodedContent);
        Assert.Contains("return_url=https://x/settings", handler.DecodedContent);
    }

    [Fact]
    public async Task Cancel_sets_cancel_at_period_end_on_the_subscription()
    {
        var (provider, handler) = Build(SubscriptionResponse);

        await provider.CancelSubscriptionAsync("sub_1");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v1/subscriptions/sub_1", handler.Uri!.AbsolutePath);
        Assert.Contains("cancel_at_period_end=true", handler.DecodedContent); // runs out the paid period
    }

    // ---- Webhook: signature ----

    [Fact]
    public void A_bad_signature_is_invalid()
    {
        const string payload = """{"id":"evt_1","object":"event","type":"checkout.session.completed","data":{"object":{}}}""";
        var signedWithWrongSecret = StripeSignature(payload, "whsec_wrong");

        var parse = ParseProvider().ParseWebhook(payload, signedWithWrongSecret);

        Assert.Equal(WebhookParseResult.InvalidSignature, parse.Result);
        Assert.Null(parse.Event);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_signature_is_invalid(string? signature)
    {
        var parse = ParseProvider().ParseWebhook("{}", signature);
        Assert.Equal(WebhookParseResult.InvalidSignature, parse.Result);
    }

    // ---- Webhook: mapping ----

    [Fact]
    public void A_completed_subscription_checkout_maps_to_CheckoutCompleted()
    {
        const string payload = """
            {"id":"evt_1","object":"event","type":"checkout.session.completed","data":{"object":{
              "id":"cs_1","object":"checkout.session","status":"complete","payment_status":"paid","mode":"subscription",
              "customer":"cus_1","subscription":"sub_1",
              "metadata":{"household_id":"hh_1","product":"SubscriptionMonthly"}}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        var e = parse.Event!;
        Assert.Equal("evt_1", e.EventId);
        Assert.Equal(PaymentEventKind.CheckoutCompleted, e.Kind);
        Assert.Equal("hh_1", e.HouseholdId);
        Assert.Equal("cus_1", e.BillingCustomerId);
        Assert.Equal("sub_1", e.SubscriptionId);
        Assert.Equal(BillingProduct.SubscriptionMonthly, e.Product);
        Assert.NotNull(e.PeriodEnd);        // a provisional period the subscription.updated event corrects
        Assert.Null(e.AmountMicros);        // a subscription grants no credit here
    }

    [Fact]
    public void A_completed_pack_checkout_maps_to_the_face_value_not_the_taxed_total()
    {
        const string payload = """
            {"id":"evt_2","object":"event","type":"checkout.session.completed","data":{"object":{
              "id":"cs_2","object":"checkout.session","status":"complete","payment_status":"paid","mode":"payment",
              "customer":"cus_1","amount_total":1099,
              "metadata":{"household_id":"hh_1","product":"CreditPack10"}}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.CheckoutCompleted, e.Kind);
        Assert.Equal(BillingProduct.CreditPack10, e.Product);
        Assert.Equal(10_000_000, e.AmountMicros); // face value ($10), NOT amount_total (1099 = $10.99 with tax)
        Assert.Null(e.SubscriptionId);
        Assert.Null(e.PeriodEnd);
    }

    [Fact]
    public void An_incomplete_checkout_session_is_ignored()
    {
        const string payload = """
            {"id":"evt_3","object":"event","type":"checkout.session.completed","data":{"object":{
              "id":"cs_3","object":"checkout.session","status":"open","mode":"subscription",
              "metadata":{"household_id":"hh_1","product":"SubscriptionMonthly"}}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        Assert.Null(parse.Event); // verified but nothing to do — an "open" session isn't complete
    }

    [Fact]
    public void A_complete_but_UNPAID_checkout_is_ignored()
    {
        // An async method (ACH/SEPA/Klarna) completes the session with payment_status "unpaid" while
        // settlement is pending — it must NOT grant until the funds land (the grant arrives later via
        // async_payment_succeeded). Gating on Status alone would grant before payment settles.
        const string payload = """
            {"id":"evt_unpaid","object":"event","type":"checkout.session.completed","data":{"object":{
              "id":"cs_u","object":"checkout.session","status":"complete","payment_status":"unpaid","mode":"payment",
              "customer":"cus_1","metadata":{"household_id":"hh_1","product":"CreditPack10"}}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        Assert.Null(parse.Event); // verified but not paid yet — no grant
    }

    [Fact]
    public void An_async_payment_succeeded_event_grants_once_it_settles()
    {
        // The delayed-settlement follow-up: it carries a now-paid session, so it maps like a paid checkout.
        const string payload = """
            {"id":"evt_async","object":"event","type":"checkout.session.async_payment_succeeded","data":{"object":{
              "id":"cs_a","object":"checkout.session","status":"complete","payment_status":"paid","mode":"payment",
              "customer":"cus_1","metadata":{"household_id":"hh_1","product":"CreditPack10"}}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.CheckoutCompleted, e.Kind);
        Assert.Equal(BillingProduct.CreditPack10, e.Product);
        Assert.Equal(10_000_000, e.AmountMicros); // grants the face value now that it's settled
    }

    [Fact]
    public void A_checkout_with_unknown_product_metadata_is_ignored()
    {
        const string payload = """
            {"id":"evt_4","object":"event","type":"checkout.session.completed","data":{"object":{
              "id":"cs_4","object":"checkout.session","status":"complete","payment_status":"paid","mode":"subscription",
              "metadata":{"household_id":"hh_1","product":"NotARealProduct"}}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        Assert.Null(parse.Event); // can't tell what was bought — don't guess
    }

    [Fact]
    public void A_subscription_update_maps_the_period_from_the_item_and_the_cancel_flag()
    {
        // Basil moved current_period_end onto the ITEM — a mapping reading the (deprecated) top-level field
        // would silently get null. 1790000000 = 2026-09-21T…Z.
        const string payload = """
            {"id":"evt_5","object":"event","type":"customer.subscription.updated","data":{"object":{
              "id":"sub_1","object":"subscription","customer":"cus_1","cancel_at_period_end":true,
              "items":{"object":"list","data":[{"id":"si_1","object":"subscription_item","current_period_end":1790000000}]}}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.SubscriptionUpdated, e.Kind);
        Assert.Equal("sub_1", e.SubscriptionId);
        Assert.Equal("cus_1", e.BillingCustomerId);
        Assert.True(e.CancelAtPeriodEnd);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), e.PeriodEnd);
    }

    [Fact]
    public void A_subscription_created_event_maps_like_an_update()
    {
        const string payload = """
            {"id":"evt_6","object":"event","type":"customer.subscription.created","data":{"object":{
              "id":"sub_2","object":"subscription","customer":"cus_2","cancel_at_period_end":false,
              "items":{"object":"list","data":[{"id":"si_2","object":"subscription_item","current_period_end":1790000000}]}}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.SubscriptionUpdated, e.Kind); // created + updated share one handler arm
        Assert.Equal("sub_2", e.SubscriptionId);
        Assert.False(e.CancelAtPeriodEnd);
    }

    [Fact]
    public void A_subscription_deletion_maps_to_SubscriptionCancelled()
    {
        const string payload = """
            {"id":"evt_7","object":"event","type":"customer.subscription.deleted","data":{"object":{
              "id":"sub_1","object":"subscription","customer":"cus_1"}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.SubscriptionCancelled, e.Kind);
        Assert.Equal("sub_1", e.SubscriptionId);
    }

    [Fact]
    public void A_failed_invoice_maps_to_PaymentFailed_resolved_by_customer()
    {
        const string payload = """
            {"id":"evt_8","object":"event","type":"invoice.payment_failed","data":{"object":{
              "id":"in_1","object":"invoice","customer":"cus_1"}}}
            """;

        var e = Parse(payload).Event!;

        Assert.Equal(PaymentEventKind.PaymentFailed, e.Kind);
        Assert.Equal("cus_1", e.BillingCustomerId);
    }

    [Fact]
    public void A_refund_is_verified_but_ignored_for_now()
    {
        // charge.refunded is deliberately not acted on in this step (tax-and-product-aware clawback is
        // deferred) — it must verify and be acked, NOT 400-retried.
        const string payload = """
            {"id":"evt_9","object":"event","type":"charge.refunded","data":{"object":{
              "id":"ch_1","object":"charge","customer":"cus_1","amount_refunded":500}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result);
        Assert.Null(parse.Event);
    }

    [Fact]
    public void An_unhandled_event_type_is_verified_but_ignored()
    {
        const string payload = """
            {"id":"evt_10","object":"event","type":"payment_intent.succeeded","data":{"object":{
              "id":"pi_1","object":"payment_intent"}}}
            """;

        var parse = Parse(payload);

        Assert.Equal(WebhookParseResult.Verified, parse.Result); // a real provider sends many of these…
        Assert.Null(parse.Event);                                // …acked (2xx), not handled
    }
}
