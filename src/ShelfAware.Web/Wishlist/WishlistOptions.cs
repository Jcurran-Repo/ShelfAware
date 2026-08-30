namespace ShelfAware.Web.Wishlist;

/// <summary>Config for the /about wishlist. Follows the Email / Google-OAuth / Admin posture: the
/// Founder PRE-ORDER button exists only when <see cref="FounderPaymentUrl"/> is a valid https link —
/// unset (or malformed), the whole pre-order block is invisible, because a payment button that can't
/// charge is worse than none. No ValidateOnStart: an empty section is the normal state (the reserve's
/// tier picker + optional email work without it).</summary>
public class WishlistOptions
{
    public const string SectionName = "Wishlist";

    /// <summary>An external hosted-checkout link (Stripe / Lemon Squeezy) for a Founder pre-order. When
    /// set, the /about Founder card shows a "Reserve a Founder spot" button linking OUT to it — no money
    /// ever touches the app. Leave unset until payments are real.</summary>
    public string? FounderPaymentUrl { get; set; }

    /// <summary>THE one definition of "the Founder pre-order is live" — the /about page gates the button
    /// on this so the surface can't drift. Requires an absolute https URL: a config typo that isn't a
    /// real secure link renders no button rather than a broken or insecure one.</summary>
    public bool FounderPreorderConfigured =>
        Uri.TryCreate(FounderPaymentUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
