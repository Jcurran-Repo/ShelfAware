using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;

namespace ShelfAware.Tests;

public class PriceWatchTests
{
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 6, 30); // midpoint = May 31

    private static int nextPurchaseId;

    private static PurchaseFact Buy(
        int month, int day, int productId, string name, decimal paid,
        decimal qty = 1, bool dominant = true) =>
        new(++nextPurchaseId, new DateOnly(2026, month, day), productId, name, Category.Dairy, qty,
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

    [Fact]
    public void A_zero_early_price_reports_no_change_not_a_divide_by_zero() =>
        Assert.Equal(0m, new PriceMover(1, "x", EarlyUnitPrice: 0m, LateUnitPrice: 5m, Spend: 10m).ChangePct);

    [Fact]
    public void Purchases_outside_the_window_are_excluded_and_the_edges_are_in()
    {
        var result = PriceWatch.Compute(
        [
            Buy(4, 30, 1, "Milk", 9.00m), // Apr 30 — before From, out
            Buy(5, 1, 1, "Milk", 3.00m),  // May 1 — From edge, in (early)
            Buy(6, 30, 1, "Milk", 3.30m), // Jun 30 — To edge, in (late)
            Buy(7, 1, 1, "Milk", 9.00m),  // Jul 1 — after To, out
        ], From, To);

        var milk = Assert.Single(result.Movers);
        Assert.Equal(3.00m, milk.EarlyUnitPrice); // the out-of-window 9.00s never enter
        Assert.Equal(3.30m, milk.LateUnitPrice);
    }

    [Fact]
    public void The_midpoint_day_belongs_to_the_early_half()
    {
        // midpoint = May 31: a purchase ON it is early; June 1 is late.
        var result = PriceWatch.Compute(
        [
            Buy(5, 31, 1, "Milk", 3.00m),
            Buy(6, 1, 1, "Milk", 3.30m),
        ], From, To);

        var milk = Assert.Single(result.Movers);
        Assert.Equal(3.00m, milk.EarlyUnitPrice);
        Assert.Equal(3.30m, milk.LateUnitPrice);
    }

    [Fact]
    public void Each_half_uses_the_average_of_its_prices_not_the_minimum()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Milk", 2.00m), Buy(5, 20, 1, "Milk", 4.00m),  // early avg 3
            Buy(6, 5, 1, "Milk", 6.00m), Buy(6, 20, 1, "Milk", 8.00m),  // late avg 7
        ], From, To);

        var milk = Assert.Single(result.Movers);
        Assert.Equal(3.00m, milk.EarlyUnitPrice); // average of 2 and 4, not the min 2
        Assert.Equal(7.00m, milk.LateUnitPrice);
    }

    [Fact]
    public void Spend_is_price_times_quantity()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "Milk", 3.00m, qty: 4), Buy(6, 5, 1, "Milk", 3.00m, qty: 1),
        ], From, To);

        Assert.Equal(15m, Assert.Single(result.Movers).Spend); // 3*4 + 3*1, not 3/4 + 3/1
    }

    [Fact]
    public void The_overall_is_the_spend_weighted_mean_of_the_changes()
    {
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "A", 1.00m, qty: 10), Buy(6, 5, 1, "A", 2.00m, qty: 10), // +100%, spend 30
            Buy(5, 6, 2, "B", 2.00m, qty: 2), Buy(6, 6, 2, "B", 3.00m, qty: 2),   // +50%,  spend 10
            Buy(5, 7, 3, "C", 1.00m), Buy(6, 7, 3, "C", 1.00m),                   // 0%,    spend 2
        ], From, To);

        // (100*30 + 50*10 + 0*2) / (30+10+2) = 3500 / 42 = 83.3 — the SUM of weighted changes over the
        // total weight, and price*spend, not max or a ratio.
        Assert.Equal(83.3m, result.OverallPct);
    }

    [Fact]
    public void A_zero_total_weight_stays_silent_rather_than_dividing_by_zero()
    {
        // Three free items: they compare, but with no spend to weight there's nothing to divide by.
        var result = PriceWatch.Compute(
        [
            Buy(5, 5, 1, "A", 0m), Buy(6, 5, 1, "A", 0m),
            Buy(5, 6, 2, "B", 0m), Buy(6, 6, 2, "B", 0m),
            Buy(5, 7, 3, "C", 0m), Buy(6, 7, 3, "C", 0m),
        ], From, To);

        Assert.Equal(3, result.ComparedProducts);
        Assert.Null(result.OverallPct);
    }
}
