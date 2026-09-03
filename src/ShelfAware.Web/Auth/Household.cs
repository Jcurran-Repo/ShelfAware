namespace ShelfAware.Web.Auth;

/// <summary>The tenancy unit: a group of accounts sharing one pantry. Pantry rows carry
/// <see cref="Id"/> as a plain value (the pantry DB has no FK into this auth-side table).</summary>
public class Household
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";

    /// <summary>Uppercase share code another person enters at registration to join this household, or
    /// null when there is no code — which is the default and the resting state.
    ///
    /// A code is a bearer credential for an entire pantry, so it's a deliberate, transient act rather than
    /// a standing fixture: a household is created without one, one is minted only when a member asks for
    /// it, and it disappears again the moment its last use is redeemed or a member clears it. Possession
    /// of the code IS the authorization to join, so it's generated from a CSPRNG.
    ///
    /// NULL, not "": the unique index on this column admits any number of NULLs but only a single "", so
    /// a second code-less household would collide with the first the moment it was saved.</summary>
    public string? InviteCode { get; set; }

    /// <summary>When the code stops working, or null for never.
    ///
    /// A code is a bearer credential for someone's entire pantry, and it used to be a permanent one:
    /// anyone who ever saw it — a screenshot, a forwarded message, a shoulder — could still be creating
    /// accounts into the household a year later, on a deployment that had otherwise closed registration.
    /// Regenerating was the only revocation, and it required knowing you had a reason to.</summary>
    public DateTimeOffset? InviteExpiresAt { get; set; }

    /// <summary>How many times the code may be redeemed, or null for unlimited. The point of a limit is
    /// that inviting one person shouldn't hand out a key that admits a crowd.</summary>
    public int? InviteMaxUses { get; set; }

    /// <summary>How many times the current code has been redeemed. Reset when the code is regenerated.</summary>
    public int InviteUseCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>The household's entitlement tier — <see cref="HouseholdTier.Free"/> by default, set to
    /// <see cref="HouseholdTier.Founder"/> when the operator grants it from /admin. Drives the AI
    /// meter's exemption (Founder = unlimited-but-recorded) and, once billing lands, the paid tiers.
    /// See <c>docs/subscription-plan.md</c> and <see cref="HouseholdTier"/>. Operator-set only — there
    /// is no self-service write path, and it is never touched by "delete my data" (an entitlement must
    /// not be grantable or wipeable by its beneficiary).</summary>
    public HouseholdTier Tier { get; set; } = HouseholdTier.Free;

    /// <summary>When Founder was granted — for the admin roster and a "Founder since …" thank-you
    /// badge — or null when the household is not a Founder. Set alongside <see cref="Tier"/> by the
    /// admin grant, and cleared when Founder is revoked.</summary>
    public DateTimeOffset? FounderSince { get; set; }

    // ---- Subscription state (phase 3 — docs/subscription-plan.md §6). The subscription attaches to the
    // HOUSEHOLD (the tenancy unit; AI allowance is shared like the pantry), so its provider identifiers
    // and period state live here. All null/false on a household that has never subscribed — which is the
    // resting state, and exactly what a pre-billing box reads back. Written only by the webhook handler
    // (phase-3 step 2) from verified provider events, never by the household itself.

    /// <summary>The payment provider's CUSTOMER id for this household, or null before anyone subscribes.
    /// Keys the customer portal (cancel/card management) and the provider-side receipts; set on the first
    /// checkout and kept across renewals. It is the provider's own id, not an email — §6 keys the customer
    /// on the purchaser's already-verified account email, but this is the opaque handle the portal link
    /// needs.</summary>
    public string? BillingCustomerId { get; set; }

    /// <summary>The provider's SUBSCRIPTION id for the household's active (or most recent) subscription, or
    /// null when there has never been one. Used to look a subscription up and to cancel it via the provider
    /// API (the purchaser-departure lifecycle, §6). A cancelled-then-resubscribed household gets a new id.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>When the current paid period ends — the next renewal on an active subscription, or the
    /// date access runs out on one set to cancel. Null when there is no subscription. Drives "your plan
    /// renews on …" / "access until …". Stored from the provider's UTC period, never derived from
    /// server-local <c>DateTime.Today</c> (the TZ gotcha). ⚠️ Does NOT drive the monthly allowance — that
    /// keys on the CALENDAR MONTH (<c>CreditLedger.PeriodFor</c>), so an annual subscriber still gets a
    /// monthly drip; see <see cref="AllowanceGrantedForPeriod"/> (§4).</summary>
    public DateTimeOffset? SubscriptionRenewsAt { get; set; }

    /// <summary>True when the subscription is set to cancel at <see cref="SubscriptionRenewsAt"/> rather
    /// than renew — the member hit "cancel" and is running out the period they paid for (§6: cancel →
    /// runs out the paid period, data untouched). False by default and for an actively-renewing sub.</summary>
    public bool SubscriptionCancelAtPeriodEnd { get; set; }

    /// <summary>The CALENDAR MONTH (<c>CreditLedger.PeriodFor(now)</c> — first-of-month UTC) the current Aware
    /// monthly allowance was granted for — the idempotency marker for the lazy per-month grant (§4). Null until
    /// the first allowance is granted (and on any non-Aware household). When it differs from the current
    /// month's value, the month has rolled over: the prior allowance's unspent remainder is swept and a new
    /// allowance granted (<c>CreditLedger.EnsureCurrentAllowanceAsync</c>). ⚠️ Keyed on the calendar month, NOT
    /// <see cref="SubscriptionRenewsAt"/>, so the grant drips monthly even on annual billing. Written only by
    /// that lazy grant, via a conditional claim so two concurrent checks can't double-grant.</summary>
    public DateTimeOffset? AllowanceGrantedForPeriod { get; set; }

    /// <summary>Whether the code would be accepted right now. A method rather than a property so EF leaves
    /// it alone (it's behaviour, not a column) and so the caller has to name the clock — which is what
    /// makes expiry testable without waiting.</summary>
    public bool InviteIsUsable(DateTimeOffset now) =>
        !string.IsNullOrEmpty(InviteCode)
        && (InviteExpiresAt is null || InviteExpiresAt > now)
        && (InviteMaxUses is null || InviteUseCount < InviteMaxUses);

    /// <summary>Uses left on the code, or null when it's unlimited — for telling the user what they're
    /// about to share.</summary>
    public int? InviteUsesRemaining => InviteMaxUses is null ? null : Math.Max(0, InviteMaxUses.Value - InviteUseCount);
}
