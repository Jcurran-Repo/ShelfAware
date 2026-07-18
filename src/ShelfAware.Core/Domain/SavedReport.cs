namespace ShelfAware.Core.Domain;

/// <summary>A report configuration the household named and kept. <see cref="Query"/> holds the spec
/// in its URL form (ReportSpecUrl) — deliberately the same serialization as a shared link, so there
/// is exactly one parser, a saved row is readable in the DB, and a row written by an older version
/// degrades the way an old bookmark does (unknown values fall back to defaults) instead of failing
/// deserialization.</summary>
public class SavedReport : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public required string Name { get; set; }
    /// <summary>The ReportSpecUrl query string ("from=…&amp;to=…&amp;metric=…").</summary>
    public required string Query { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}
