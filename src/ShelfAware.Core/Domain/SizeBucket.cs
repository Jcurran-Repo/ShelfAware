namespace ShelfAware.Core.Domain;

/// <summary>
/// Grouping key for a purchase/receipt-line size string. Size is metadata, not identity — but when
/// the app DOES group by size (price series, price index, dominant-size cadence), the "sold per
/// each" family must land in one bucket: extraction writes loose produce inconsistently (usually
/// null, sometimes a literal "each"/"ea"/"1 ct"), and they're all the same buying basis. Anything
/// else groups by its trimmed, lowercased text. No unit arithmetic ("1 gal" ≠ 2 × "64 fl oz"), by
/// design — see the data-model notes in CLAUDE.md.
/// </summary>
public static class SizeBucket
{
    private static readonly HashSet<string> EachSizes =
        new(StringComparer.OrdinalIgnoreCase) { "", "each", "ea", "ea.", "per each", "1 each", "1 ct", "1ct", "loose", "single" };

    public const string EachKey = "each";

    /// <summary>The each-family collapses to <see cref="EachKey"/>; anything else groups by its
    /// trimmed, lowercased text.</summary>
    public static string Key(string? size)
    {
        var s = (size ?? "").Trim();
        return EachSizes.Contains(s) ? EachKey : s.ToLowerInvariant();
    }
}
