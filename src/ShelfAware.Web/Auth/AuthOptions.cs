namespace ShelfAware.Web.Auth;

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Whether visitors may self-register a NEW household. Default true (self-host ergonomics);
    /// a locked-down deployment sets false. Two paths stay open regardless: the very first user (bootstrap —
    /// a fresh locked deploy must be enterable) and joining an EXISTING household with a valid invite code
    /// (possession of the code is the authorization).</summary>
    public bool AllowRegistration { get; set; } = true;
}
