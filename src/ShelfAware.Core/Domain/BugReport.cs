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

    /// <summary>An optional diagnostic snapshot of the page the reporter was on — the JSON serialization
    /// of what they chose to attach: the environment (URL, viewport, browser, theme, recent client-side
    /// JS errors) and/or the page's visible content. Null = nothing attached (they opened /bugs directly,
    /// or removed every section). Captured client-side at the moment they click "Report a bug", but — like
    /// <see cref="PageUrl"/> — NEVER silently: it is shown on the form in a collapsible panel with each
    /// section independently removable, so it is only ever the reporter's own household data, attached by
    /// the reporter's own choice. The shape lives in Web (<c>BugReportSnapshot</c>); Core keeps only the
    /// stored string.</summary>
    public string? StateJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the admin PROPOSED this as fixed and handed it to the reporter to confirm; null =
    /// never proposed. Set by the admin (cross-household, <c>ReportResolutionService</c>) and cleared
    /// when the report goes resolved or is reopened. A proposal is a request, not a verdict: it only
    /// governs while <see cref="ResolvedAt"/> is still null (see <see cref="AwaitingReporter"/>).</summary>
    public DateTimeOffset? ProposedResolvedAt { get; set; }

    /// <summary>When the report was actually marked handled; null = not resolved. Two paths now write it:
    /// the admin (cross-household, <c>ReportResolutionService</c> — the "resolve anyway" override) and the
    /// REPORTER on their own report (household-scoped, <c>ReporterReportService</c> — confirming a proposal
    /// or self-resolving). Shown back to the reporting household on /bugs so filing a report isn't a
    /// one-way letterbox.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>THE one reading of "is this report dealt with" — a raw stamp check today, and the
    /// single place to grow if bug resolution ever gains conditions. Deliberately DIFFERENT from
    /// the error log's derived rule (a report doesn't recur the way an error fingerprint does);
    /// don't carry either predicate to the other side. Get-only and ignored in the model, so EF
    /// maps nothing.</summary>
    public bool Resolved => ResolvedAt is not null;

    /// <summary>The middle state: the admin proposed a fix and the reporter hasn't answered yet — so
    /// /bugs offers them "confirm fixed" / "still broken". Only meaningful while not yet resolved (a
    /// resolve leaves any lingering proposal moot, which is why the ResolvedAt guard is here and not
    /// just "proposed is set"). Get-only, ignored in the model like <see cref="Resolved"/>.</summary>
    public bool AwaitingReporter => ProposedResolvedAt is not null && ResolvedAt is null;
}
