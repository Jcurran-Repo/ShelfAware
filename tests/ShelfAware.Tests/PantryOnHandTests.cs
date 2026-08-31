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
    public void Detailed_flags_a_fresh_count_as_count_backed_and_a_prediction_as_not()
    {
        // Counted 3 today, on a rhythm → in stock AND count-backed: real evidence earns the confident ✓.
        var counted = new Product
        {
            Name = "Chicken Breast", Category = Category.Meat, IsTracked = true,
            TrackQuantity = true, QuantityOnHand = 3m, QuantityCountedAt = DateTimeOffset.Now,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-16) },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-1) },
            ],
        };
        // Bought recently, never counted → in stock by the RHYTHM only → NOT count-backed (a "likely" ✓).
        var predicted = new Product
        {
            Name = "Ground Beef", Category = Category.Meat, IsTracked = true,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-16) },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-1) },
            ],
        };

        var detailed = PantryOnHand.EdibleInStockDetailed([counted, predicted], Today);

        Assert.True(detailed.Single(d => d.Product.Name == "Chicken Breast").CountBacked);
        Assert.False(detailed.Single(d => d.Product.Name == "Ground Beef").CountBacked);
    }

    [Fact]
    public void Detailed_does_not_count_back_a_stale_count()
    {
        // Counted 5 long ago on a ~15-day rhythm → the count is long spent, so it defers to the rhythm
        // (which, bought a day ago, still reads stocked) and is NOT count-backed: a number nobody has
        // vouched for in months is not evidence any more (§13.5).
        var stale = new Product
        {
            Name = "White Rice", Category = Category.Pantry, IsTracked = true,
            TrackQuantity = true, QuantityOnHand = 5m, QuantityCountedAt = DateTimeOffset.Now.AddDays(-120),
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-16) },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-1) },
            ],
        };

        var row = Assert.Single(PantryOnHand.EdibleInStockDetailed([stale], Today));
        Assert.False(row.CountBacked);
    }

    [Fact]
    public void Detailed_drops_non_food_aisles_and_untracked_items()
    {
        // The detailed view carries the SAME aisle + tracking filter as every other on-hand method. A
        // tracked household item and an untracked edible must both be ABSENT — even though each would read
        // "in stock" on its own (the soap by its fresh count, the produce by an Unknown, not-overdue
        // rhythm), so the filter, not the on-hand test, is what excludes them. Pins that filter directly on
        // EdibleInStockDetailed so it can't be quietly re-duplicated or loosened.
        var edible = new Product
        {
            Name = "Whole Milk", Category = Category.Dairy, IsTracked = true,
            TrackQuantity = true, QuantityOnHand = 2m, QuantityCountedAt = DateTimeOffset.Now,
        };
        var trackedNonFood = new Product
        {
            Name = "Dish Soap", Category = Category.Household, IsTracked = true,
            TrackQuantity = true, QuantityOnHand = 4m, QuantityCountedAt = DateTimeOffset.Now,
        };
        var untrackedEdible = P("Bananas", Category.Produce, tracked: false);

        var names = PantryOnHand.EdibleInStockDetailed([edible, trackedNonFood, untrackedEdible], Today)
            .Select(d => d.Product.Name).ToList();

        Assert.Equal(["Whole Milk"], names);
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

    // ---- §13: a count reaches recipes directly, which is the only way census stock ever can -------

    /// <summary>Stock with no purchase history — bought before the app, elsewhere, gifted, or in one bulk
    /// run. Its status is permanently Unknown, so reading Status alone made its count invisible here.</summary>
    private static Product Counted(string name, decimal onHand, int countedDaysAgo)
    {
        var p = P(name, Category.Meat);
        p.TrackQuantity = true;
        p.QuantityOnHand = onHand;
        p.QuantityCountedAt = new DateTimeOffset(
            Today.AddDays(-countedDaysAgo).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return p;
    }

    [Fact]
    public void A_counted_item_with_no_purchase_history_is_on_hand_because_of_the_count()
    {
        // Before this it was in stock only incidentally — Unknown isn't Overdue — so a counted 12 added
        // nothing and the number was decoration. Now the count is the reason.
        var beef = Counted("Quarter Cow Ground Beef", 12m, countedDaysAgo: 5);

        Assert.Equal(["Quarter Cow Ground Beef"], PantryOnHand.EdibleInStock([beef], Today).Select(p => p.Name));
        Assert.Empty(PantryOnHand.EdibleOutOfStock([beef], Today));
    }

    [Fact]
    public void A_counted_item_eaten_down_to_none_stops_being_on_hand()
    {
        // The gap that mattered: the household told the app it had twelve, ate all twelve, and recipes
        // went on believing there was beef — because nothing here ever read the count.
        var beef = Counted("Quarter Cow Ground Beef", 0m, countedDaysAgo: 5);

        Assert.Empty(PantryOnHand.EdibleInStock([beef], Today));
        // …and it surfaces as run-out so a red recipe row can explain itself rather than just being red.
        Assert.Equal(["Quarter Cow Ground Beef"], PantryOnHand.EdibleOutOfStock([beef], Today).Select(p => p.Name));
    }

    [Fact]
    public void A_stale_count_defers_to_the_rhythm_instead_of_deciding()
    {
        // Same reason a stale count stops suppressing: a number nobody has vouched for in months is not
        // evidence. With no rhythm either, the item falls back to Unknown — which is not Overdue, so it
        // stays available rather than being declared empty on the strength of an old guess.
        var beef = Counted("Quarter Cow Ground Beef", 0m, countedDaysAgo: 200);

        Assert.Equal(["Quarter Cow Ground Beef"], PantryOnHand.EdibleInStock([beef], Today).Select(p => p.Name));
    }

    [Fact]
    public void An_expired_counted_item_is_NOT_on_hand()
    {
        // A count answers "how many", never "are they still good" — so v3.6's label beats it, exactly as it
        // beats buy-suppression. Without this the app offered to cook with food it knew was expired.
        var chicken = Counted("Chicken Breast", 3m, countedDaysAgo: 6);
        chicken.Purchases.Add(new PurchaseEvent { PurchasedAt = Today.AddDays(-30) });
        chicken.Purchases.Add(new PurchaseEvent { PurchasedAt = Today.AddDays(-20) });
        chicken.Purchases.Add(new PurchaseEvent
        { PurchasedAt = Today.AddDays(-10), ExpirationDate = Today.AddDays(-6) });

        Assert.Empty(PantryOnHand.EdibleInStock([chicken], Today, honorExpirations: true));
        Assert.Equal(["Chicken Breast"],
            PantryOnHand.EdibleOutOfStock([chicken], Today, honorExpirations: true).Select(p => p.Name));
    }

    [Fact]
    public void A_counted_item_the_household_reported_OUT_is_NOT_on_hand()
    {
        // §13.5: "an explicit OutNow beats the count outright." A count is a memory of a past look; an
        // OutNow is a statement about now. Recipes have to agree with the engine on that.
        var coffee = Counted("Coffee", 2m, countedDaysAgo: 2);
        coffee.Purchases.Add(new PurchaseEvent { PurchasedAt = Today.AddDays(-20) });
        coffee.Purchases.Add(new PurchaseEvent { PurchasedAt = Today.AddDays(-10) });
        coffee.Signals.Add(new InventorySignal
        {
            Kind = SignalKind.OutNow,
            SignaledAt = new DateTimeOffset(Today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        });

        Assert.Empty(PantryOnHand.EdibleInStock([coffee], Today));
        Assert.Equal(["Coffee"], PantryOnHand.EdibleOutOfStock([coffee], Today).Select(p => p.Name));
    }

    [Fact]
    public void The_split_agrees_with_the_two_lists_it_replaces()
    {
        // EdibleSplit exists so a caller needing both doesn't predict everything twice. It must be the
        // SAME predicate, or there'd be a third definition of on-hand for the two to disagree with.
        var counted = Counted("Quarter Cow Ground Beef", 12m, countedDaysAgo: 5);
        var empty = Counted("Canned Beans", 0m, countedDaysAgo: 5);
        var products = new[]
        {
            counted,
            empty,
            P("Whole Milk", Category.Dairy),
            P("Dish Soap", Category.Household),               // non-food, in neither
            P("Old Cereal", Category.Pantry, tracked: false), // untracked, in neither
        };

        var (inStock, outOfStock) = PantryOnHand.EdibleSplit(products, Today);

        Assert.Equal(
            PantryOnHand.EdibleInStock(products, Today).Select(p => p.Name),
            inStock.Select(p => p.Name));
        Assert.Equal(
            PantryOnHand.EdibleOutOfStock(products, Today).Select(p => p.Name),
            outOfStock.Select(p => p.Name));
        Assert.Contains("Quarter Cow Ground Beef", inStock.Select(p => p.Name));
        Assert.Contains("Canned Beans", outOfStock.Select(p => p.Name));
    }

    [Fact]
    public void A_fresh_count_beats_a_rhythm_that_says_overdue()
    {
        // §13.5's principle reaching recipes: real evidence beats a learned guess. The rhythm has this
        // long overdue; the household counted three yesterday.
        var coffee = new Product
        {
            Name = "Coffee",
            Category = Category.Beverage,
            TrackQuantity = true,
            QuantityOnHand = 3m,
            QuantityCountedAt = new DateTimeOffset(Today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 1) },
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 10) },
            ],
        };

        Assert.Equal(["Coffee"], PantryOnHand.EdibleInStock([coffee], Today).Select(p => p.Name));
        Assert.Empty(PantryOnHand.EdibleOutOfStock([coffee], Today));
    }

    [Fact]
    public void A_stale_positive_count_with_an_overdue_rhythm_defers_to_the_rhythm()
    {
        // The mirror of A_fresh_count_beats_a_rhythm_that_says_overdue, and the pair that makes both
        // real: identical long-overdue coffee, identical count of three — only the attestation age
        // differs. A ~9-day rhythm spends three in ~27 days, so a 200-day-old count is long distrusted
        // and the rhythm's verdict (out) stands.
        var coffee = new Product
        {
            Name = "Coffee",
            Category = Category.Beverage,
            TrackQuantity = true,
            QuantityOnHand = 3m,
            QuantityCountedAt = new DateTimeOffset(Today.AddDays(-200).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 1) },
                new PurchaseEvent { PurchasedAt = new DateOnly(2020, 1, 10) },
            ],
        };

        Assert.Empty(PantryOnHand.EdibleInStock([coffee], Today));
        Assert.Equal(["Coffee"], PantryOnHand.EdibleOutOfStock([coffee], Today).Select(p => p.Name));
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

    [Fact]
    public void Expired_items_move_from_on_hand_to_out_of_stock_only_when_expirations_are_honored()
    {
        // Bought yesterday (recent, so the cadence says "stocked"), but the label ran out today-1:
        // an expired chicken must not count as on-hand chicken for recipes — yet ONLY when the
        // household opted into expiration tracking; off means fully dormant, matching the engine.
        var chicken = new Product
        {
            Name = "Chicken Breast",
            Category = Category.Meat,
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-10), ExpirationDate = Today.AddDays(-1) },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-20) },
            ],
        };

        Assert.Empty(PantryOnHand.EdibleInStock([chicken], Today, honorExpirations: true));
        Assert.Equal(["Chicken Breast"],
            PantryOnHand.EdibleOutOfStock([chicken], Today, honorExpirations: true).Select(p => p.Name).ToList());

        Assert.Equal(["Chicken Breast"],
            PantryOnHand.EdibleInStock([chicken], Today).Select(p => p.Name).ToList());
        Assert.Empty(PantryOnHand.EdibleOutOfStock([chicken], Today));
    }
}
