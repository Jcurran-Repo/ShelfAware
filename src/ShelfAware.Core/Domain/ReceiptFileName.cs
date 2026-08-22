namespace ShelfAware.Core.Domain;

/// <summary>Turns a receipt's merchant name into a filename-safe slug — the one definition shared by the
/// "export fixture labels" download on /receipts and the receipt-image download endpoint, so the two can't
/// drift on how a receipt is named on disk.</summary>
public static class ReceiptFileName
{
    /// <summary>The merchant lower-cased, with every non-alphanumeric character turned to '-' and the ends
    /// trimmed; the empty string when there's no merchant. Callers add their own prefix, date and
    /// extension (the fixture export wants "&lt;merchant&gt;-&lt;date&gt;", the download "receipt-…").</summary>
    public static string MerchantSlug(string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return "";
        return new string(merchant.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
    }

    /// <summary>The download filename (WITHOUT extension) for a receipt's saved copy: "receipt", the
    /// merchant slug when there is one, and the purchase date — or the receipt id when it's undated. The
    /// endpoint appends the page's own extension, or ".zip".</summary>
    public static string ForDownload(string? merchant, DateOnly? purchasedAt, int id)
    {
        var slug = MerchantSlug(merchant);
        return "receipt"
            + (slug.Length > 0 ? $"-{slug}" : "")
            + (purchasedAt is { } d ? $"-{d:yyyy-MM-dd}" : $"-{id}");
    }
}
