namespace ShelfAware.Web.Diagnostics;

/// <summary>One distinct error the app has logged, deduplicated by fingerprint — the machine half
/// of in-app problem reporting. Lives in auth.db, NOT the pantry DB, because errors are OPERATOR
/// data: no household owns one, none appears in a data export or falls to "delete all my data",
/// and the admin viewer needs no tenancy bypass to read them. Rows carry no household attribution
/// by design, matching the app's logging discipline (what users said stays out of the log).</summary>
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
}
