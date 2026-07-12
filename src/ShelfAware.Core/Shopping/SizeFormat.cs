namespace ShelfAware.Core.Shopping;

/// <summary>
/// Normalizes a free-text package size for <em>display</em> only ("12 Count" / "12  COUNT" → "12 count").
/// Receipts disagree on casing and spacing, but grouping already ignores case and whitespace (see
/// <see cref="Domain.SizeBucket"/>), so this is purely cosmetic — it never changes which purchases roll
/// up or the predicted cadence. Walmart's unit words (fl oz, ct, gal, lb) are conventionally lowercase.
/// </summary>
public static class SizeFormat
{
    /// <summary>Trim, collapse internal whitespace, and lowercase. Returns null for null/blank input.</summary>
    public static string? Normalize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var collapsed = string.Join(' ', size.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToLowerInvariant();
    }
}
