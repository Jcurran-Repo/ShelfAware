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
}
