using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

public class PantryOnHandTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static Product P(string name, Category category, bool tracked = true) =>
        new() { Name = name, Category = category, IsTracked = tracked };

    [Fact]
    public void Keeps_edible_in_stock_items_and_drops_the_non_food_aisles()
    {
        var products = new[]
        {
            P("Whole Milk", Category.Dairy),
            P("Dish Soap", Category.Household),
            P("Chicken Jerky Dog Treats", Category.PetCare),
            P("Shampoo", Category.PersonalCare),
        };

        var onHand = PantryOnHand.EdibleInStock(products, Today).Select(p => p.Name).ToList();

        Assert.Equal(["Whole Milk"], onHand);
    }

    [Fact]
    public void Drops_untracked_items()
    {
        Assert.Empty(PantryOnHand.EdibleInStock([P("Whole Milk", Category.Dairy, tracked: false)], Today));
    }

    [Fact]
    public void Drops_items_the_engine_thinks_you_ran_out_of()
    {
        // Two purchases ~9 days apart, years ago → the predictor marks it long overdue.
        var coffee = new Product
        {
            Name = "Coffee",
            Category = Category.Beverage,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 1) },
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 10) },
            ],
        };

        Assert.Empty(PantryOnHand.EdibleInStock([coffee], Today));
    }
}
