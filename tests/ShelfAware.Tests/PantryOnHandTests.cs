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

    [Fact]
    public void Out_of_stock_is_the_exact_complement_for_edible_tracked_items()
    {
        // Long-overdue coffee (the EdibleInStock drop case) is exactly what EdibleOutOfStock surfaces —
        // while non-food, untracked, and in-stock items stay out of BOTH lists / the in-stock list only.
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
        var products = new[]
        {
            coffee,
            P("Whole Milk", Category.Dairy),                    // in stock → not "out"
            P("Dish Soap", Category.Household),                 // non-food → never surfaced
            P("Old Cereal", Category.Pantry, tracked: false),   // untracked → never surfaced
        };

        Assert.Equal(["Coffee"], PantryOnHand.EdibleOutOfStock(products, Today).Select(p => p.Name).ToList());
    }

    [Fact]
    public void Untracked_edibles_are_surfaced_separately_and_non_food_stays_out()
    {
        var products = new[]
        {
            P("93% Lean Ground Beef", Category.Meat, tracked: false), // the "you have this, but untracked" case
            P("Whole Milk", Category.Dairy),                          // tracked → not in this pool
            P("Old Sponges", Category.Household, tracked: false),     // untracked but non-food → never surfaced
        };

        Assert.Equal(["93% Lean Ground Beef"], PantryOnHand.EdibleUntracked(products).Select(p => p.Name).ToList());
        // And it stays out of BOTH stock pools — this pool is the only place untracked items appear.
        Assert.Empty(PantryOnHand.EdibleInStock([products[0]], Today).ToList());
        Assert.Empty(PantryOnHand.EdibleOutOfStock([products[0]], Today).ToList());
    }
}
