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
}
