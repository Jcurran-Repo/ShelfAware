namespace ShelfAware.Web.Auth;

/// <summary>SMTP settings for the app's outbound account email (today: the password reset).
/// Config-gated like Google OAuth: leave the whole "Email" section absent and the feature simply
/// doesn't exist — the sign-in page shows no reset link, /Account/ForgotPassword explains itself,
/// and Settings keeps its honest "no email reset" wording. Configure it and all three flip together.
/// Gmail example: SmtpHost smtp.gmail.com, SmtpPort 587, SmtpUser = the gmail address,
/// SmtpPassword = an app password (requires 2-Step Verification), From = the same address.</summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string? SmtpHost { get; set; }

    /// <summary>587 (STARTTLS) is the modern default; 465 (implicit TLS) also works — the client
    /// negotiates by port.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Optional as a PAIR with <see cref="SmtpPassword"/>: an authenticated relay sets both,
    /// a localhost relay sets neither. One without the other fails startup validation. Note the
    /// connection still REQUIRES TLS either way (STARTTLS on any port but 465, failing closed) — a
    /// plaintext-only relay won't work, deliberately.</summary>
    public string? SmtpUser { get; set; }

    public string? SmtpPassword { get; set; }

    /// <summary>The From address. Required whenever <see cref="SmtpHost"/> is set.</summary>
    public string? From { get; set; }

    public string FromName { get; set; } = "Shelf Aware";

    /// <summary>THE one definition of "this deployment can send email" — every gate asks this
    /// (the sign-in page's reset link, the ForgotPassword page, Settings' wording), so the
    /// surfaces can't drift apart.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(From);

    /// <summary>True when nothing in the section is set at all — the valid "feature off" state.
    /// Anything between this and <see cref="IsConfigured"/> is a typo'd deploy and fails startup.</summary>
    public bool IsWhollyAbsent =>
        string.IsNullOrWhiteSpace(SmtpHost) && string.IsNullOrWhiteSpace(From)
        && string.IsNullOrWhiteSpace(SmtpUser) && string.IsNullOrWhiteSpace(SmtpPassword);

    /// <summary>User and password go together — both set, or both absent.</summary>
    public bool CredentialsPaired =>
        string.IsNullOrWhiteSpace(SmtpUser) == string.IsNullOrWhiteSpace(SmtpPassword);
}
