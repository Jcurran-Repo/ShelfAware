using System.Globalization;
using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class ReceiptFileNameTests
{
    [Theory]
    [InlineData("Walmart", "walmart")]
    [InlineData("Sam's Club", "sam-s-club")]
    [InlineData("  Costco  ", "costco")]        // surrounding whitespace trims to nothing
    [InlineData("Trader Joe's #123", "trader-joe-s--123")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("!!!", "")]                     // all-symbol slugs collapse to empty, not a bare "-"
    public void MerchantSlug_lowercases_and_dashes_non_alphanumerics(string? merchant, string expected)
    {
        Assert.Equal(expected, ReceiptFileName.MerchantSlug(merchant));
    }

    [Theory]
    [InlineData("Walmart", "2026-08-13", 5, "receipt-walmart-2026-08-13")]
    [InlineData("Costco", null, 5, "receipt-costco-5")]         // undated → id fallback, not a bare date
    [InlineData(null, "2026-08-13", 7, "receipt-2026-08-13")]   // no merchant → no slug segment (no double dash)
    [InlineData(null, null, 42, "receipt-42")]
    [InlineData("!!!", "2026-08-13", 9, "receipt-2026-08-13")]  // symbol-only merchant drops out cleanly
    public void ForDownload_names_a_receipt_by_merchant_and_date_or_id(string? merchant, string? date, int id, string expected)
    {
        var purchasedAt = date is null
            ? (DateOnly?)null
            : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expected, ReceiptFileName.ForDownload(merchant, purchasedAt, id));
    }
}
