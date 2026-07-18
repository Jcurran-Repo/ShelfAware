namespace ShelfAware.Core.Reporting;

/// <summary>One product's price movement across the window: what one unit cost in the early half vs
/// the late half (dominant-size paid prices only), and how much of the window's spend it carried.</summary>
public sealed record PriceMover(
    int ProductId, string ProductName, decimal EarlyUnitPrice, decimal LateUnitPrice, decimal Spend)
{
    public decimal ChangePct => EarlyUnitPrice == 0 ? 0 : (LateUnitPrice - EarlyUnitPrice) / EarlyUnitPrice * 100;
}

/// <summary>The household's personal grocery inflation over a window.</summary>
/// <param name="OverallPct">The spend-weighted average unit-price change, or null when too few
/// products were bought in both halves to say anything honest.</param>
/// <param name="ComparedProducts">How many products the index actually rests on — disclosed, because
/// "your groceries are up 6%" based on three items is a different claim than based on forty.</param>
public sealed record PriceWatchResult(
    decimal? OverallPct, int ComparedProducts, int TotalProducts, IReadOnlyList<PriceMover> Movers);

/// <summary>
/// A personal price index: how much more (or less) THIS household's groceries cost, weighted by what
/// it actually spends — a $0.40 lime jump matters less than a $0.40 milk jump if milk is a third of
/// the basket. The method mirrors how CPI weights a basket, applied to the household's own receipts:
/// split the window in half, average each product's dominant-size PAID unit prices per half (the
/// like-with-like rule — estimates and off-size buys never enter), take the spend-weighted mean of
/// the changes. Products bought in only one half can't show a change and sit out (disclosed).
/// </summary>
public static class PriceWatch
{
    /// <summary>Products the index refuses to speak below — one item's jump isn't "your groceries".</summary>
    public const int MinComparedProducts = 3;

    public static PriceWatchResult Compute(IReadOnlyList<PurchaseFact> purchases, DateOnly from, DateOnly to)
    {
        var window = purchases.Where(f => f.Date >= from && f.Date <= to).ToList();
        var midpoint = from.AddDays((to.DayNumber - from.DayNumber) / 2);

        var movers = new List<PriceMover>();
        var totalProducts = 0;
        foreach (var product in window.GroupBy(f => f.ProductId))
        {
            totalProducts++;
            var priced = product.Where(f => f.InDominantSizeBucket && f.PaidUnitPrice is not null).ToList();
            var early = priced.Where(f => f.Date <= midpoint).Select(f => f.PaidUnitPrice!.Value).ToList();
            var late = priced.Where(f => f.Date > midpoint).Select(f => f.PaidUnitPrice!.Value).ToList();
            if (early.Count == 0 || late.Count == 0) continue; // bought in one half only — no change to report

            movers.Add(new PriceMover(
                product.Key,
                product.First().ProductName,
                Math.Round(early.Average(), 2),
                Math.Round(late.Average(), 2),
                product.Sum(f => (f.Price ?? 0) * f.Quantity)));
        }

        decimal? overall = null;
        var totalWeight = movers.Sum(m => m.Spend);
        if (movers.Count >= MinComparedProducts && totalWeight > 0)
        {
            overall = Math.Round(movers.Sum(m => m.ChangePct * m.Spend) / totalWeight, 1);
        }

        return new PriceWatchResult(
            overall,
            movers.Count,
            totalProducts,
            movers.OrderByDescending(m => m.ChangePct).ToList());
    }
}
