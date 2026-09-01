namespace ShelfAware.Web.Billing;

/// <summary>Which payment adapter backs <see cref="IPaymentProvider"/>. <see cref="Fake"/> is the
/// deterministic in-process adapter used for local dev and tests (no external calls, no keys);
/// <see cref="StripeManagedPayments"/> is the real merchant-of-record adapter that lands in phase-3
/// step 5 (docs/subscription-plan.md §6). The value is the SEAM — both names exist so the choice is
/// config-driven — but only <see cref="Fake"/> is wired today; selecting the real one before its
/// adapter exists fails startup rather than half-working (see Program.cs).</summary>
public enum PaymentProviderKind
{
    Fake = 0,
    StripeManagedPayments = 1,
}

/// <summary>
/// Everything the payment seam needs, bound from the <c>"Payments"</c> config section. Config-gated the
/// way <c>GraphQL:Enabled</c> / <c>Admin</c> / <c>Email</c> are (docs/subscription-plan.md §7): with the
/// section absent, <see cref="Enabled"/> is false, no <see cref="IPaymentProvider"/> is registered, and
/// billing simply does not exist — today's behaviour exactly. Nothing here is used until a later phase-3
/// step wires a caller; step 1 defines the config surface of the seam so those steps bind values rather
/// than add fields.
///
/// Secrets (<see cref="ApiKey"/>, <see cref="WebhookSigningSecret"/>) live in user-secrets / the
/// deployment's protected config like every other secret — never committed.
/// </summary>
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>The master gate. False (the default, and the state of a box with no <c>Payments</c>
    /// section) means the feature does not exist: no provider registered, no checkout, no webhook, no
    /// upsell. Set true only on the pay-to-play box (§7) — the family box and the demo droplet stay off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which adapter to use when <see cref="Enabled"/>. Defaults to <see cref="PaymentProviderKind.Fake"/>
    /// so an enabled dev box works with no external account; the pay-to-play box sets
    /// <see cref="PaymentProviderKind.StripeManagedPayments"/> once that adapter ships (step 5).</summary>
    public PaymentProviderKind Provider { get; set; } = PaymentProviderKind.Fake;

    /// <summary>The provider's secret API key (checkout/portal/cancel calls). Null for the fake, which
    /// makes no external calls. Consumed by the real adapter (step 5).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The provider's webhook signing secret — the shared secret the raw-body webhook endpoint
    /// (step 2) verifies each event's HMAC against. The fake signs and verifies with it too, so its wire
    /// format is real crypto. REQUIRED whenever <see cref="Enabled"/> (enforced at startup in Program.cs):
    /// there is deliberately NO default — a hard-coded fallback would be a forgery trapdoor in a public
    /// repo, so an enabled box must configure one, fake or real.</summary>
    public string? WebhookSigningSecret { get; set; }

    // ---- Product/price identifiers (the provider's ids for each purchasable thing, §6). Consumed by the
    // real adapter's checkout (steps 3/5) to open the right hosted page; the fake ignores them (it echoes
    // the requested BillingProduct straight back). Left null until the pay-to-play box's products exist.

    /// <summary>The provider price/variant id for the $2.99/mo subscription.</summary>
    public string? MonthlyPriceId { get; set; }

    /// <summary>The provider price/variant id for the $27.99/yr subscription.</summary>
    public string? AnnualPriceId { get; set; }

    /// <summary>The provider price/variant id for the $5 credit pack.</summary>
    public string? CreditPack5PriceId { get; set; }

    /// <summary>The provider price/variant id for the $10 credit pack.</summary>
    public string? CreditPack10PriceId { get; set; }

    /// <summary>The provider price/variant id for the $20 credit pack.</summary>
    public string? CreditPack20PriceId { get; set; }

    /// <summary>THE one definition of "payments exist on this box" — every surface that shows or hides a
    /// billing affordance asks this, so they can't drift (the <see cref="EmailOptions.IsConfigured"/>
    /// pattern). Step 1 is just <see cref="Enabled"/>; step 5 tightens it to also require the real
    /// provider's key + price ids so a half-configured pay-to-play box fails fast rather than 500ing at
    /// the first checkout.</summary>
    public bool IsConfigured => Enabled;
}
