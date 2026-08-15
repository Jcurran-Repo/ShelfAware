namespace ShelfAware.Web.Diagnostics;

/// <summary>One distinct error the app has logged, deduplicated by fingerprint — the machine half
/// of in-app problem reporting. Lives in auth.db, NOT the pantry DB, because errors are OPERATOR
/// data: no household owns one, none appears in a data export or falls to "delete all my data",
/// and the admin viewer needs no tenancy bypass to read them. Rows carry no household attribution
/// by design, matching the app's logging discipline (what users said stays out of the log) —
/// though exception TEXT can incidentally carry household-derived strings (a receipt path inside
/// an IO error, say), which is acceptable for an admin-only table that never reaches an export.</summary>
public class ErrorLogEntry
{
    public int Id { get; set; }

    /// <summary>Hash of category + exception type + message TEMPLATE (not the rendered message),
    /// so "product 12" and "product 99" are one error counted twice, not two rows.</summary>
    public required string Fingerprint { get; set; }

    public required string Level { get; set; }
    public required string Category { get; set; }
    public string? ExceptionType { get; set; }

    /// <summary>The most recent rendered message (truncated) — the freshest concrete instance of
    /// this error.</summary>
    public required string LastMessage { get; set; }

    /// <summary>The most recent exception's full text — message, stack, inner exceptions —
    /// truncated to keep one noisy error from bloating the file.</summary>
    public string? ExceptionDetail { get; set; }

    public int Count { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>The resolved-THROUGH mark; null = never resolved. ⚠️ The admin's resolve stamps the
    /// <see cref="LastSeenAt"/> they were LOOKING AT, not the click's wall clock — so an occurrence
    /// that arrived between their render and their click, or one captured earlier but persisted
    /// later by the background writer, lands AFTER the mark and reopens the row. Stamping "now"
    /// here is how unseen recurrences get silently swallowed; don't.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>THE one reading of "is this error dealt with": resolved means nothing has been
    /// seen past what the admin resolved through. A recurrence bumps <see cref="LastSeenAt"/> past
    /// the mark and the row is active again by DERIVATION — the capture pipeline never learns
    /// resolution exists, so a recurring error can never sit hidden behind a stale resolve.
    /// Get-only and ignored in the model, so EF maps nothing.</summary>
    public bool Resolved => ResolvedAt is { } at && LastSeenAt <= at;
}
