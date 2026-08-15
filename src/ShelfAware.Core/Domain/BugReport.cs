namespace ShelfAware.Core.Domain;

/// <summary>A household member's "this looks wrong" — the human half of in-app problem reporting
/// (the machine half is the error log, which lives outside the pantry DB because errors are
/// operator data, not household data). Household-owned like any pantry row: the household files
/// it, sees it, exports it, and "delete all my data" takes it; only the config-designated admin
/// reads across households, through the one gated reader.</summary>
public class BugReport : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }

    /// <summary>What the reporter typed. The form refuses a blank — a report with nothing in it
    /// gives the admin nothing to act on.</summary>
    public required string Body { get; set; }

    /// <summary>Where it happened — pre-filled from the page the reporter came from and editable.
    /// Shown on the form, never captured silently.</summary>
    public string? PageUrl { get; set; }

    /// <summary>The reporter's sign-in email at filing time, denormalized: accounts live in
    /// auth.db, so there is no FK to point at — and the admin needs a "who" even if the account
    /// is later removed.</summary>
    public string? ReportedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the admin marked this handled; null = still open. The app's ONE admin-written
    /// field on household data — set and cleared only by <c>ReportResolutionService</c>'s
    /// column-scoped update, and shown back to the reporting household on /bugs so filing a report
    /// isn't a one-way letterbox.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>THE one reading of "is this report dealt with" — a raw stamp check today, and the
    /// single place to grow if bug resolution ever gains conditions. Deliberately DIFFERENT from
    /// the error log's derived rule (a report doesn't recur the way an error fingerprint does);
    /// don't carry either predicate to the other side. Get-only and ignored in the model, so EF
    /// maps nothing.</summary>
    public bool Resolved => ResolvedAt is not null;
}
