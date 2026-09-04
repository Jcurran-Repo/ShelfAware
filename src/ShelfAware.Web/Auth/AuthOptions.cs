namespace ShelfAware.Web.Auth;

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Whether visitors may self-register a NEW household. Default true (self-host ergonomics);
    /// a locked-down deployment sets false. Two paths stay open regardless: the very first user (bootstrap —
    /// a fresh locked deploy must be enterable) and joining an EXISTING household with a valid invite code
    /// (possession of the code is the authorization).</summary>
    public bool AllowRegistration { get; set; } = true;

    /// <summary>Whether a new account must confirm its email address before it can sign in. Default false —
    /// the self-host and family posture (the family box verifies email at the Cloudflare Access edge, so
    /// app-level confirmation is redundant there). Turned on for the PUBLIC demo box, which has no edge gate:
    /// it makes a real, distinct, VERIFIED inbox the price of an account (distinct is already
    /// <c>RequireUniqueEmail</c>; this adds verified).
    ///
    /// ⚠️ Drives Identity's global <c>SignIn.RequireConfirmedAccount</c> (Program.cs), so enabling it blocks
    /// sign-in for EVERY unconfirmed account — including any that registered before the flag was on. That's
    /// why it's per-box, and why a box with existing accounts backfills <c>EmailConfirmed = 1</c> before
    /// flipping it (see docs/subscription-plan.md §10). ⚠️ Requires the <c>Email:</c> section configured —
    /// with no mailer nobody could ever confirm, so startup validation refuses the combination.</summary>
    public bool RequireEmailConfirmation { get; set; }

    /// <summary>An EXPLICIT box-wide cap on how many new accounts may be created per day, or null (the
    /// default) to defer to <see cref="EffectiveDailyAccountCreationLimit"/> — which on a
    /// confirmation-required (public) box falls back to <see cref="DefaultDailyAccountCreationLimit"/> so it's
    /// never accidentally unbounded, and on a direct-registration box means no limit (the self-host/family
    /// posture). Set explicitly to raise, lower, or (with a high value) deliberately run a
    /// confirmation-required box uncapped. Counted off <see cref="AppUser.CreatedOn"/> across all
    /// registration paths. On a DIRECT box only the new-household path is blocked at the cap (a join with a
    /// valid invite code is never turned away); on a CONFIRMATION box the household is chosen later at the
    /// chooser, so the cap gates EVERY registration there (an invitee registers under it, then joins
    /// uncapped). Startup validation refuses 0 or negative (a typo, not "no accounts today").</summary>
    public int? DailyAccountCreationLimit { get; set; }

    /// <summary>The default daily account-creation cap applied to a confirmation-required (public) box that
    /// configures none — so a demo box can never be left accidentally unbounded, even if the operator forgets
    /// to set one. Deliberately generous for a small deployment and overridable by an explicit
    /// <see cref="DailyAccountCreationLimit"/>.</summary>
    public const int DefaultDailyAccountCreationLimit = 10;

    /// <summary>The cap the limiter actually enforces. An explicit <see cref="DailyAccountCreationLimit"/>
    /// always wins; otherwise a confirmation-required box falls back to <see cref="DefaultDailyAccountCreationLimit"/>
    /// (never accidentally unbounded on a public box) and a direct box stays uncapped (null). One accessible
    /// definition so the limiter, the startup log, and any future reader agree on the effective number.</summary>
    public int? EffectiveDailyAccountCreationLimit =>
        DailyAccountCreationLimit ?? (RequireEmailConfirmation ? DefaultDailyAccountCreationLimit : null);

    /// <summary>How long a freshly generated invite code stays usable, in days. Null (the default) means
    /// never expires — the behaviour every existing code already has, so upgrading changes nothing until
    /// someone regenerates.
    ///
    /// Set it on any deployment where a leaked code matters. A code admits its bearer to a household's
    /// whole pantry and bypasses Auth:AllowRegistration by design ("possession of the code is the
    /// authorization"), so without an expiry the blast radius of one screenshot is permanent.</summary>
    public int? InviteCodeLifetimeDays { get; set; }

    /// <summary>The load-bearing startup rule: requiring email confirmation only makes sense if the box can
    /// actually send email — otherwise a new account can never confirm and never sign in, and the box boots
    /// broken. Program.cs enforces it against the resolved <see cref="EmailOptions.IsConfigured"/> at
    /// startup; a static so the rule has one home and can be pinned, rather than living only in a boot-time
    /// lambda.</summary>
    public static bool EmailConfirmationSatisfiable(bool requireEmailConfirmation, bool emailConfigured)
        => !requireEmailConfirmation || emailConfigured;
}
