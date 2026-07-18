namespace ShelfAware.Core.Reporting;

/// <summary>One time bucket on the x-axis. Buckets are continuous over the window (empty ones exist
/// with zero/null values) so the axis is honest time, not "the periods that happened to have data".</summary>
public sealed record ReportBucket(DateOnly Start, string Label);

/// <summary>One chart series. Values align with the result's buckets; null = no observation in that
/// bucket, which matters for averages (UnitPrice draws a gap, not a zero — zero would read as "free").
/// Additive metrics use 0 for empty buckets (nothing was bought, and that IS the value).</summary>
public sealed record ReportSeries(string Label, IReadOnlyList<decimal?> Values)
{
    public decimal Total => Values.Sum(v => v ?? 0);
}

/// <summary>What the engine hands the chart: buckets, series, and the honesty flags the UI must obey.</summary>
public sealed record ReportResult
{
    public required IReadOnlyList<ReportBucket> Buckets { get; init; }
    public required IReadOnlyList<ReportSeries> Series { get; init; }

    /// <summary>Whether the series partition the data — stacking and a grand total are only honest
    /// when they do. False for tag splits (overlap) and per-product quantities (unit mismatch).</summary>
    public bool Stackable { get; init; }

    /// <summary>Whether summing a series (or the whole window) means anything. True for spend,
    /// counts, meals, calories; false for unit price (an average's sum is noise).</summary>
    public bool Additive { get; init; }

    /// <summary>The window's grand total, for additive metrics where the series partition the data
    /// (or there's a single series); null when a total would double-count or mix units.</summary>
    public decimal? Total { get; init; }

    /// <summary>Total for the equal-length window ending the day before From — the "vs last period"
    /// stat. Only computed when the spec asked and Total itself is honest.</summary>
    public decimal? PreviousTotal { get; init; }

    /// <summary>Disclosed exclusions ("3 purchases had no price and aren't in the spend"), or null.
    /// A report that silently drops rows reads as "covered everything" when it didn't.</summary>
    public string? Note { get; init; }

    /// <summary>What kind of number the values are — the UI formats from this ($, count, kcal).</summary>
    public ReportUnit Unit { get; init; }
}
