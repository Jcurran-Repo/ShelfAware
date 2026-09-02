using ShelfAware.Web.Billing;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The safety gate on the FAKE payment provider (which mints Aware/credits with NO charge): it may run ONLY
/// in Development. Mirrors <c>DevAuthTests</c>' truth-table pin — the load-bearing rows are that a
/// NON-Development box never arms the fake, whatever <c>Payments:Provider</c> says, so a config typo can't
/// hand out free subscriptions on a live box (bug from the phase-3 gate).
/// </summary>
public class PaymentsGateTests
{
    [Theory]
    // Non-Development: the fake is NEVER allowed, whatever the provider string (the safety property).
    [InlineData(true, null, false, false)]                    // Provider unset (defaults to Fake) in prod
    [InlineData(true, "Fake", false, false)]
    [InlineData(true, "Stripe", false, false)]                // a typo still resolves to "not allowed" in prod
    [InlineData(true, "StripeManagedPayments", false, false)] // the real provider isn't the fake anyway
    // Development: the fake is allowed unless the real provider is explicitly selected.
    [InlineData(true, null, true, true)]                      // unset defaults to Fake → allowed in dev
    [InlineData(true, "Fake", true, true)]
    [InlineData(true, "StripeManagedPayments", true, false)]  // real provider chosen — not the fake
    [InlineData(true, "stripemanagedpayments", true, false)]  // case-insensitive match on the real provider
    // Payments disabled: never, anywhere.
    [InlineData(false, "Fake", true, false)]
    [InlineData(false, null, true, false)]
    public void FakeProviderAllowed_truth_table(bool enabled, string? provider, bool isDevelopment, bool expected) =>
        Assert.Equal(expected, PaymentsOptions.FakeProviderAllowed(enabled, provider, isDevelopment));
}
