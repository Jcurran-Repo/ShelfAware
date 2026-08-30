namespace ShelfAware.Web.Wishlist;

/// <summary>Config for the /about wishlist. Follows the Email / Google-OAuth / Admin posture: the
/// "back it early" supporter button exists only when <see cref="SupporterPaymentUrl"/> is a valid https
/// link — unset (or malformed), the whole block is invisible, because a payment button that can't charge
/// is worse than none. No ValidateOnStart: an empty section is the normal state (the reserve's tier
/// picker + optional email work without it).</summary>
public class WishlistOptions
{
    public const string SectionName = "Wishlist";

    /// <summary>An external hosted-checkout link (Stripe / Lemon Squeezy) for an early-supporter "back it
    /// early" contribution. When set, the /about page shows a "Back it early" button linking OUT to it —
    /// no money ever touches the app. Deliberately NOT tied to the Founder tier (that's the operator's
    /// free gift to grant); people who back it early are just prospective supporters. Leave unset until
    /// payments are real.</summary>
    public string? SupporterPaymentUrl { get; set; }

    /// <summary>THE one definition of "the back-it-early button is live" — the /about page gates the
    /// button on this so the surface can't drift. Requires an absolute https URL: a config typo that
    /// isn't a real secure link renders no button rather than a broken or insecure one.</summary>
    public bool SupporterLinkConfigured =>
        Uri.TryCreate(SupporterPaymentUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
