namespace ShelfAware.Web.Wishlist;

/// <summary>One person's signal that they'd use a HOSTED Reginald — the pre-launch demand list.
/// Lives in auth.db, NOT the pantry DB, because it's OPERATOR data: no household owns it, it never
/// appears in a household export or falls to "delete all my data", and the admin viewer needs no
/// tenancy bypass to read it. Same placement and rationale as
/// <see cref="ShelfAware.Web.Diagnostics.ErrorLogEntry"/>.
///
/// <para>The raw ROW COUNT is a soft interest number by design: a public one-click counter can't be
/// made fraud-proof without friction that defeats its purpose — a localStorage flag stops honest
/// double-clicks and a per-IP rate limit brakes hammering, and that is the honest ceiling. The
/// DISTINCT EMAILS are the signal worth trusting, and the reason to collect them: a real launch list
/// you can notify when hosting is ready.</para></summary>
public class WishlistEntry
{
    public int Id { get; set; }

    /// <summary>The tier the person said they'd want — a <see cref="WishlistTiers"/> key
    /// ("shelf"/"aware"/"souschef"/"founder"), validated against that catalog before it's stored so a
    /// tampered form can't write an arbitrary string. Marketing INTENT, not an entitlement grant —
    /// deliberately a string, not <see cref="ShelfAware.Web.Auth.HouseholdTier"/> (which has no
    /// Aware/Sous Chef yet).</summary>
    public required string Tier { get; set; }

    /// <summary>Optional "notify me" address. Null/empty = an anonymous interest click. When present it
    /// is the deduplicable, notifiable signal; the app never emails it automatically (you export the
    /// list and mail people at launch), so this feature needs no mail server.</summary>
    public string? Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
