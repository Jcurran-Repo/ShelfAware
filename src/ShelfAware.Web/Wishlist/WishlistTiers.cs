namespace ShelfAware.Web.Wishlist;

/// <summary>The tiers offered on the /about reserve — the pre-launch intent picker. Names and prices
/// MIRROR docs/subscription-plan.md (the source of truth for the plan); kept as a small in-code catalog
/// because that doc isn't machine-readable and the reserve needs the copy at render time. ⚠️ If the plan
/// changes a price, update it here too. When subscriptions ship and
/// <see cref="ShelfAware.Web.Auth.HouseholdTier"/> grows Aware/Sous Chef, this stays the MARKETING
/// catalog (display copy) while the enum stays the ENTITLEMENT — they answer different questions.</summary>
public sealed record ReserveTier(string Key, string Name, string Price, string Blurb);

public static class WishlistTiers
{
    /// <summary>All four tiers, in ladder order. "founder" is the operator's free thank-you tier in the
    /// plan; on the reserve it's the early-supporter intent, and its optional PAID pre-order button is
    /// config-gated separately (<see cref="WishlistOptions.FounderPreorderConfigured"/>).</summary>
    public static readonly IReadOnlyList<ReserveTier> All =
    [
        new("shelf", "Shelf", "Free",
            "The full app — track what you buy, predict what's running low, build your list. Self-host it free, forever."),
        new("aware", "Aware", "$2.99/mo · $27.99/yr",
            "Managed AI on our keys: scan receipts, chat, recipe help, count from a shelf photo — nothing to set up."),
        new("souschef", "Sous Chef", "~$4.99/mo (coming later)",
            "Everything in Aware, plus the hands-free voice cook-along."),
        new("founder", "Founder", "Early supporters",
            "The thank-you tier for the people who back it early."),
    ];

    /// <summary>THE gate a stored tier must pass — the /about handler validates the posted key against
    /// this before recording, so a tampered form can't write an arbitrary string.</summary>
    public static bool IsValidKey(string? key) => key is not null && All.Any(t => t.Key == key);

    public static ReserveTier? ByKey(string? key) => All.FirstOrDefault(t => t.Key == key);
}
