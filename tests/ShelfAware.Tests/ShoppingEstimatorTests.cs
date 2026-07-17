using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class ShoppingEstimatorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 1);

    private static DateOnly D(int dayOffset) => Day0.AddDays(dayOffset);

    private static Product ProductWith(params (int day, decimal qty)[] purchases) =>
        new()
        {
            Id = 1,
            Name = "Test",
            Category = Category.Pantry,
            Purchases = purchases.Select(p => new PurchaseEvent { ProductId = 1, PurchasedAt = D(p.day), Quantity = p.qty }).ToList(),
        };

    private static PredictionResult Prediction(PredictionStatus status, DateOnly? due) =>
        new() { ProductId = 1, Status = status, DueDate = due, Basis = "test" };

    [Fact]
    public void TypicalQuantity_IsMedianOfPurchaseQuantities()
    {
        var product = ProductWith((0, 1m), (10, 2m), (20, 3m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(30)), D(20), unitPrice: null);

        Assert.Equal(2m, e.TypicalQuantity);
    }

    [Fact]
    public void TypicalQuantity_EvenCount_AveragesTheTwoMiddleValues()
    {
        var product = ProductWith((0, 2m), (10, 4m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: null);

        Assert.Equal(3m, e.TypicalQuantity);
    }

    [Fact]
    public void TypicalQuantity_DefaultsToOne_WhenNoPurchases()
    {
        var product = ProductWith();

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Unknown, due: null), D(0), unitPrice: null);

        Assert.Equal(1m, e.TypicalQuantity);
    }

    [Fact]
    public void ExpectedCost_IsUnitPriceTimesTypicalQuantity()
    {
        var product = ProductWith((0, 1m), (10, 2m), (20, 3m)); // median qty 2

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(30)), D(20), unitPrice: 3.50m);

        Assert.Equal(3.50m, e.UnitPrice);
        Assert.Equal(7.00m, e.ExpectedCost);
    }

    [Fact]
    public void RecommendedQuantity_RoundsUpWholeUnitItems_KeepingTypicalExact()
    {
        // Bought 1 then 2 → median 1.5. You can't buy half a box, so recommend 2; keep 1.5 as the stat.
        var product = ProductWith((0, 1m), (10, 2m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: 2m);

        Assert.Equal(1.5m, e.TypicalQuantity);
        Assert.Equal(2m, e.RecommendedQuantity);
        Assert.Equal(4m, e.ExpectedCost); // cost uses the rounded buy-quantity: 2 × $2
    }

    [Fact]
    public void RecommendedQuantity_StaysFractional_ForWeightPricedItems()
    {
        // Weight-priced (2.31 lb, 1.8 lb) → genuinely fractional, so don't round it to a whole number.
        var product = ProductWith((0, 2.31m), (10, 1.8m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: null);

        Assert.Equal(2.055m, e.TypicalQuantity);
        Assert.Equal(2.055m, e.RecommendedQuantity); // unchanged — a real fraction, not a count
    }

    [Fact]
    public void ExpectedCost_IsNull_WhenNoPriceKnown()
    {
        var product = ProductWith((0, 1m), (10, 1m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: null);

        Assert.Null(e.UnitPrice);
        Assert.Null(e.ExpectedCost);
    }

    [Fact]
    public void LastPurchased_IsMostRecentPurchaseDate()
    {
        var product = ProductWith((0, 1m), (20, 1m), (10, 1m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(40)), D(25), unitPrice: null);

        Assert.Equal(D(20), e.LastPurchased);
    }

    [Fact]
    public void DaysUntil_IsSignedDistanceFromDueDate()
    {
        var product = ProductWith((0, 1m), (20, 1m));

        var overdue = ShoppingEstimator.For(product, Prediction(PredictionStatus.Overdue, D(20)), D(25), unitPrice: null);
        Assert.Equal(D(20), overdue.NextBuyDate);
        Assert.Equal(-5, overdue.DaysUntil); // 5 days past due

        var upcoming = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(40)), D(25), unitPrice: null);
        Assert.Equal(15, upcoming.DaysUntil);
    }

    [Fact]
    public void NoDueDate_LeavesNextBuyAndDaysUntilNull()
    {
        var product = ProductWith((0, 1m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Unknown, due: null), D(5), unitPrice: 2m);

        Assert.Null(e.NextBuyDate);
        Assert.Null(e.DaysUntil);
        // cost is still computable from quantity + price even without a date
        Assert.Equal(2m, e.ExpectedCost);
    }

    [Fact]
    public void RecommendedSize_IsCarriedFromPrediction_AndNormalizedForDisplay()
    {
        var product = ProductWith((0, 1m), (10, 1m));
        var prediction = Prediction(PredictionStatus.Stocked, D(20)) with { RecommendedSize = "1 GAL" };

        var e = ShoppingEstimator.For(product, prediction, D(10), unitPrice: null);

        Assert.Equal("1 gal", e.RecommendedSize);
    }

    [Fact]
    public void RecommendedQuantity_SumsSameDayLines_IntoOneTripTotal()
    {
        // 3 Gala + 3 Honeycrisp on one receipt is a six-apple TRIP — the list should say 6, not the
        // per-line 3. Same-day lines sum (the predictor's stock-up factor reads trips the same way).
        var product = ProductWith((0, 3m), (0, 3m), (10, 3m), (10, 3m), (20, 3m), (20, 2m));

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(30)), D(20), unitPrice: 1m);

        Assert.Equal(6m, e.TypicalQuantity);      // trip totals 6, 6, 5 → median 6
        Assert.Equal(6m, e.RecommendedQuantity);
        Assert.Equal(6m, e.ExpectedCost);         // costs a trip's worth, not a line's worth
    }

    [Fact]
    public void UsualVariety_IsMostBoughtVariety_WithPlusNHint_AndBreakdownsListEveryKind()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Apples",
            Category = Category.Produce,
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Variety = "Gala" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Variety = "Honeycrisp" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Variety = "Gala", Brand = "Rainier" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Variety = "Fuji" },
            ],
        };

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(30)), D(20), unitPrice: null);

        Assert.Equal("Gala +2", e.UsualVariety);
        // Most-bought first, ties alphabetical — the tap-for-detail list behind the "+N".
        Assert.Equal(["Gala ×2", "Fuji ×1", "Honeycrisp ×1"], e.VarietiesBought);
        Assert.Equal(["Rainier ×1"], e.BrandsBought);
    }

    [Fact]
    public void UsualVariety_IsNull_WhenNoPurchaseCarriesAVariety()
    {
        var product = ProductWith((0, 1m), (10, 1m)); // ProductWith leaves Variety null

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: null);

        Assert.Null(e.UsualVariety);
        Assert.Empty(e.VarietiesBought);
    }

    [Fact]
    public void UsualBrand_IsMostBoughtBrand_WithPlusNHintAcrossBrands()
    {
        var product = new Product
        {
            Id = 1,
            Name = "Bread",
            Category = Category.Pantry,
            Purchases =
            [
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(0), Brand = "Nature's Own" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(10), Brand = "Nature's Own" },
                new PurchaseEvent { ProductId = 1, PurchasedAt = D(20), Brand = "Sara Lee" },
            ],
        };

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(30)), D(20), unitPrice: null);

        Assert.Equal("Nature's Own +1", e.UsualBrand);
    }

    [Fact]
    public void UsualBrand_IsNull_WhenNoPurchaseCarriesABrand()
    {
        var product = ProductWith((0, 1m), (10, 1m)); // ProductWith leaves Brand null

        var e = ShoppingEstimator.For(product, Prediction(PredictionStatus.Stocked, D(20)), D(10), unitPrice: null);

        Assert.Null(e.UsualBrand);
    }

    [Fact]
    public void UsualBrandOf_PrefersHigherCount_ThenAlphabetical()
    {
        var purchases = new[]
        {
            new PurchaseEvent { Brand = "Zebra" },
            new PurchaseEvent { Brand = "Acme" },
            new PurchaseEvent { Brand = "Acme" },
            new PurchaseEvent { Brand = null },
            new PurchaseEvent { Brand = "  " },
        };

        Assert.Equal("Acme +1", ShoppingEstimator.UsualBrandOf(purchases));
    }
}
