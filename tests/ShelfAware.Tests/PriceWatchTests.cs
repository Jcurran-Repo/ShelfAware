using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class PriceWatchTests
{
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 6, 30); // midpoint = May 31

    private static PurchaseFact Buy(
        int month, int day, int productId, string name, decimal paid,
        decimal qty = 1, bool dominant = true) =>
        new(new DateOnly(2026, month, day), productId, name, Category.Dairy, qty,
            paid, paid, dominant, []);

    [Fact]
    public void Weights_changes_by_spend_not_by_item()
    {
        // Milk: $3 -> $3.30 (+10%), $12.60 of the basket. Gum: $1 -> $2 (+100%), $3 of it.
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Milk", 3.00m), Buy(5, 20, 1, "Milk", 3.00m),
            Buy(6, 5, 1, "Milk", 3.30m), Buy(6, 20, 1, "Milk", 3.30m),
            Buy(5, 6, 2, "Gum", 1.00m), Buy(6, 6, 2, "Gum", 2.00m),
            Buy(5, 7, 3, "Eggs", 4.00m), Buy(6, 7, 3, "Eggs", 4.00m), // flat, keeps count >= 3
        ], From, To);

        Assert.NotNull(result.OverallPct);
        // Unweighted mean would be (10+100+0)/3 = 36.7%; spend-weighting pulls it far down.
        Assert.True(result.OverallPct < 25m,
            $"Expected a spend-weighted figure well under the naive mean, got {result.OverallPct}%");
        Assert.Equal(3, result.ComparedProducts);
    }

    [Fact]
    public void A_product_bought_in_only_one_half_sits_out_but_is_counted_in_the_disclosure()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Milk", 3.00m), Buy(6, 5, 1, "Milk", 3.30m),
            Buy(6, 20, 9, "Watermelon", 6.00m), // June only — no early price to compare
        ], From, To);

        Assert.Single(result.Movers);
        Assert.Equal(1, result.ComparedProducts);
        Assert.Equal(2, result.TotalProducts);
        Assert.Null(result.OverallPct); // 1 compared product < the floor — no headline claim
    }

    [Fact]
    public void Off_size_and_estimated_prices_never_enter_the_index()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Limes", 0.25m), Buy(6, 5, 1, "Limes", 0.25m),
            Buy(6, 6, 1, "Limes", 8.00m, dominant: false), // the bag must not read as inflation
        ], From, To);

        var limes = Assert.Single(result.Movers);
        Assert.Equal(0m, limes.ChangePct);
    }

    [Fact]
    public void Movers_sort_increases_first_and_carry_the_per_unit_change()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Milk", 3.00m), Buy(6, 5, 1, "Milk", 3.60m),   // +20%
            Buy(5, 6, 2, "Eggs", 5.00m), Buy(6, 6, 2, "Eggs", 4.00m),   // -20%
            Buy(5, 7, 3, "Rice", 2.00m), Buy(6, 7, 3, "Rice", 2.00m),   // flat
        ], From, To);

        Assert.Equal(["Milk", "Rice", "Eggs"], result.Movers.Select(m => m.ProductName));
        Assert.Equal(20m, result.Movers[0].ChangePct);
        Assert.Equal(-20m, result.Movers[^1].ChangePct);
    }
}
