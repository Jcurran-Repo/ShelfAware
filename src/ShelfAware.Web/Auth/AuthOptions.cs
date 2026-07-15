namespace ShelfAware.Web.Auth;

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Whether visitors may self-register a NEW household. Default true (self-host ergonomics);
    /// a locked-down deployment sets false. Two paths stay open regardless: the very first user (bootstrap —
    /// a fresh locked deploy must be enterable) and joining an EXISTING household with a valid invite code
    /// (possession of the code is the authorization).</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>How long a freshly generated invite code stays usable, in days. Null (the default) means
    /// never expires — the behaviour every existing code already has, so upgrading changes nothing until
    /// someone regenerates.
    ///
    /// Set it on any deployment where a leaked code matters. A code admits its bearer to a household's
    /// whole pantry and bypasses Auth:AllowRegistration by design ("possession of the code is the
    /// authorization"), so without an expiry the blast radius of one screenshot is permanent.</summary>
    public int? InviteCodeLifetimeDays { get; set; }
}
