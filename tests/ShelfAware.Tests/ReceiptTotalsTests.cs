using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class ReceiptTotalsTests
{
    private static ReceiptLine Line(decimal qty, decimal? unitPrice) => new()
    {
        RawText = "x",
        NormalizedName = "x",
        Quantity = qty,
        UnitPrice = unitPrice,
    };

    [Fact]
    public void LineTotal_IsUnitPriceTimesQuantity_IncludingWeightQuantities()
    {
        Assert.Equal(6.28m, ReceiptTotals.LineTotal(Line(2m, 3.14m)));
        // Weight-priced: quantity is the weight (2.31 lb at $1.99/lb) — same formula.
        Assert.Equal(4.5969m, ReceiptTotals.LineTotal(Line(2.31m, 1.99m)));
        Assert.Null(ReceiptTotals.LineTotal(Line(3m, null)));
    }

    [Fact]
    public void Summarize_SumsPricedLines_AndCountsUnpricedOnesHonestly()
    {
        var summary = ReceiptTotals.Summarize(
        [
            Line(1m, 12.00m),
            Line(2m, 3.14m),
            Line(5m, null), // extraction couldn't price it — excluded from the total, counted
        ]);

        Assert.Equal(18.28m, summary.Total);
        Assert.Equal(2, summary.PricedLines);
        Assert.Equal(1, summary.UnpricedLines);
        Assert.Equal(3, summary.LineCount);
    }

    [Fact]
    public void Summarize_OfNoLines_IsZeroNotAnError()
    {
        var summary = ReceiptTotals.Summarize([]);

        Assert.Equal(0m, summary.Total);
        Assert.Equal(0, summary.LineCount);
    }
}
