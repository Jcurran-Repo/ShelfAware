using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

/// <summary>
/// Populates the database with a synthetic-but-realistic demo catalog so a fresh (public) deploy isn't a
/// ghost town while a visitor decides whether to add their own key and receipts. The data is modeled on the
/// SHAPE of real shopping, not any real person's receipts, and every purchase date is generated relative to
/// "today" at seed time, so the demo never goes stale.
///
/// Deliberately MESSY — the whole pitch is "order found in the chaos", so intervals jitter, one item runs
/// out ahead of its rebuy rhythm (burn rate diverges), one is a stock-up, one has a vacation gap the engine
/// trims as an outlier. Clean, metronomic data would make the predictor look like a calendar; each of these
/// "hero" cases puts a real engine behaviour on stage (see the comments on each).
///
/// Guarded: it only seeds an EMPTY catalog, so it can never clobber real data.
/// </summary>
public sealed class DemoDataSeeder(IDbContextFactory<ShelfAwareDbContext> dbFactory)
{
    public sealed record Result(bool Seeded, int Products, int Purchases, int Recipes, string Message);

    public async Task<Result> SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Products.AnyAsync(ct))
            return new Result(false, 0, 0, 0, "Sample data skipped — the catalog already has items.");

        var products = BuildProducts(DateOnly.FromDateTime(DateTime.Today));
        db.Products.AddRange(products);

        db.ExcludedFoods.AddRange(
            new ExcludedFood { Value = "mushrooms" },
            new ExcludedFood { Value = "cilantro" });
        db.GroceryExtras.AddRange(
            new GroceryExtra { Name = "Aluminium foil" },
            new GroceryExtra { Name = "Birthday candles" });

        var originals = BuildOriginalRecipes();
        db.Recipes.AddRange(originals);
        await db.SaveChangesAsync(ct); // populate recipe Ids so the variant can point at its parent

        var chicken = originals.First(r => r.Name.Contains("Chicken", StringComparison.OrdinalIgnoreCase));
        db.Recipes.Add(BuildChickenThighVariant(chicken.Id));
        await db.SaveChangesAsync(ct);

        var purchases = products.Sum(p => p.Purchases.Count);
        var recipeCount = await db.Recipes.CountAsync(ct);
        return new Result(true, products.Count, purchases, recipeCount,
            $"Loaded {products.Count} sample products, {purchases} purchases, and {recipeCount} recipes.");
    }

    // ---- Products -----------------------------------------------------------

    // One seeded product: its aisle + descriptive tags, and the trips that bought it (each as days-ago +
    // quantity), plus any live signals and "also works as" substitutes.
    private sealed record Seed(
        string Name, Category Category, string? Brand, string Size, string[] Tags,
        (int DaysAgo, decimal Qty)[] Buys,
        (int DaysAgo, SignalKind Kind)[]? Signals = null,
        string[]? AlsoWorksAs = null);

    private static List<Product> BuildProducts(DateOnly today)
    {
        // Fixed seed → the "messy" jitter is identical every run (reproducible demo + testable).
        var rng = new Random(20260705);

        // Jittered trips: `count` buys ending `lastAgo` days ago, each gap = baseGap ± spread. This is the
        // messy real-world rhythm the median/IQR has to see through.
        (int, decimal)[] Trips(int count, int baseGap, int spread, int lastAgo, decimal qty = 1)
        {
            var buys = new List<(int, decimal)>();
            var off = lastAgo;
            for (var i = 0; i < count; i++)
            {
                buys.Add((off, qty));
                off += baseGap + rng.Next(-spread, spread + 1);
            }
            return [.. buys];
        }

        var seeds = new List<Seed>
        {
            // ---- HERO cases: each demonstrates one engine behaviour ----

            // Cereal-week milk: jumpy intervals. Median stays sane but the IQR spread WIDENS the DueSoon
            // window, so a noisy staple warns earlier than a metronomic one.
            new("Whole Milk", Category.Dairy, "Great Value", "1 gal", ["Breakfast"],
                [(7, 1), (13, 1), (18, 1), (27, 1), (33, 1), (40, 1), (51, 1)]),

            // Dogs eat faster than we rebuy: OutNow keeps landing ~14 days after each 26-day rebuy, so BURN
            // RATE (14d) diverges from the rebuy rhythm (26d) and takes over the prediction.
            new("Dry Dog Food", Category.PetCare, "Pedigree", "30 lb", ["Dog"],
                [(8, 1), (34, 1), (62, 1), (88, 1)],
                Signals: [(20, SignalKind.OutNow), (46, SignalKind.OutNow), (74, SignalKind.OutNow)]),

            // Stock-up: the last trip bought 3× the usual, so StockUpFactor stretches the due date out
            // instead of nagging on the one-pack cadence.
            new("Paper Towels", Category.Household, "Bounty", "6 rolls", ["Paper"],
                [(5, 3), (26, 1), (48, 1), (70, 1)]),

            // Vacation gap: one 45-day interval among ~12-day ones. MedianWithTrim drops it (> 3× median) so
            // the cadence stays honest at ~12 days.
            new("Ground Coffee", Category.Beverage, "Folgers", "30.5 oz", ["Breakfast"],
                [(7, 1), (19, 1), (31, 1), (76, 1), (88, 1), (100, 1)]),

            // Marked out right now → pinned Overdue with a "Marked out of stock" note (signal override).
            new("Dish Soap", Category.Household, "Dawn", "19 oz", ["Cleaning"],
                [(30, 1), (58, 1)], Signals: [(2, SignalKind.OutNow)]),

            // Flagged running low by hand → floored to DueSoon even though the stats say Stocked.
            new("Paper Napkins", Category.Household, "Vanity Fair", "100 ct", ["Paper"],
                Trips(3, 40, 5, 6), Signals: [(1, SignalKind.RunningLow)]),

            // ---- Overdue by the stats (populate the dashboard's "overdue") ----
            new("Sandwich Bread", Category.Pantry, "Nature's Own", "20 oz", ["Bakery"],
                [(11, 1), (18, 1), (26, 1), (33, 1), (40, 1)]),
            new("Bananas", Category.Produce, null, "bunch", ["Fruit", "Snack"],
                [(6, 1), (11, 1), (17, 1), (22, 1), (28, 1)]),

            // ---- Recipe-backing staples, kept in stock so the saved recipes read "Ready to make" ----
            new("Chicken Breast", Category.Meat, "Tyson", "2.5 lb", ["Protein"], Trips(5, 12, 3, 3),
                AlsoWorksAs: ["chicken", "chicken cutlet"]),
            new("White Rice", Category.Pantry, "Great Value", "5 lb", ["Grain"], Trips(4, 30, 6, 8)),
            new("Broccoli", Category.Produce, null, "12 oz", ["Vegetable"], Trips(5, 9, 2, 2)),
            new("Ground Beef", Category.Meat, null, "1 lb", ["Protein"], Trips(6, 10, 3, 4),
                AlsoWorksAs: ["ground meat"]),
            new("Yellow Onion", Category.Produce, null, "3 lb bag", ["Vegetable"], Trips(4, 24, 5, 5)),
            new("Bell Peppers", Category.Produce, null, "3 ct", ["Vegetable"], Trips(5, 11, 3, 4)),
            new("Flour Tortillas", Category.Pantry, "Mission", "8 ct", ["Bakery"], Trips(4, 16, 4, 6)),
            new("Shredded Cheddar", Category.Dairy, "Great Value", "8 oz", ["Cheese"], Trips(5, 13, 3, 5)),

            // ---- Background catalog: varied cadences + jitter for a healthy status spread ----
            new("Large Eggs", Category.Dairy, "Great Value", "18 ct", ["Breakfast", "Protein"], Trips(6, 9, 2, 8)),
            new("Greek Yogurt", Category.Dairy, "Chobani", "32 oz", ["Breakfast"], Trips(5, 11, 3, 9)),
            new("Salted Butter", Category.Dairy, "Land O'Lakes", "1 lb", ["Baking"], Trips(4, 26, 5, 4)),
            new("Bacon", Category.Meat, "Oscar Mayer", "16 oz", ["Protein", "Breakfast"], Trips(4, 18, 4, 15)),
            new("Baby Spinach", Category.Produce, null, "10 oz", ["Vegetable", "Salad"], Trips(5, 8, 2, 7)),
            new("Roma Tomatoes", Category.Produce, null, "1 lb", ["Vegetable"], Trips(5, 9, 3, 3)),
            new("Spaghetti", Category.Pantry, "Barilla", "16 oz", ["Grain"], Trips(4, 21, 5, 6)),
            new("Marinara Sauce", Category.Pantry, "Rao's", "24 oz", ["Canned"], Trips(4, 22, 5, 4)),
            new("Peanut Butter", Category.Pantry, "Jif", "40 oz", ["Snack"], Trips(3, 34, 6, 12)),
            new("Breakfast Cereal", Category.Pantry, "General Mills", "18 oz", ["Breakfast"], Trips(6, 7, 2, 5)),
            new("Tortilla Chips", Category.Pantry, "Tostitos", "13 oz", ["Snack"], Trips(5, 10, 3, 9)),
            new("Orange Juice", Category.Beverage, "Simply", "52 oz", ["Breakfast"], Trips(5, 12, 3, 11)),
            new("Frozen Pizza", Category.Frozen, "DiGiorno", "1 ct", ["Dinner"], Trips(4, 15, 4, 3)),
            new("Frozen Blueberries", Category.Frozen, "Great Value", "16 oz", ["Fruit", "Breakfast"], Trips(3, 24, 6, 20)),
            new("Toilet Paper", Category.Household, "Charmin", "12 rolls", ["Paper"], Trips(4, 24, 4, 18)),
            new("Hand Soap", Category.PersonalCare, "Softsoap", "11 oz", ["Bath"], Trips(3, 30, 6, 10)),
            new("Toothpaste", Category.PersonalCare, "Colgate", "6 oz", ["Bath"], Trips(3, 40, 7, 26)),
            new("Cat Litter", Category.PetCare, "Fresh Step", "20 lb", ["Cat"], Trips(4, 17, 4, 12)),

            // ---- "Still learning": one buy, so cadence is honestly Unknown ----
            new("Sriracha Sauce", Category.Pantry, "Huy Fong", "17 oz", ["Condiment"], [(14, 1)]),
            new("Olive Oil", Category.Pantry, "Bertolli", "25 oz", ["Oil"], [(9, 1)]),
        };

        return seeds.Select(s => new Product
        {
            Name = s.Name,
            Category = s.Category,
            IsTracked = true,
            Tags = [.. s.Tags.Select(t => new ProductTag { Value = t })],
            Substitutes = [.. (s.AlsoWorksAs ?? []).Select(v => new ProductSubstitute { Value = v })],
            Purchases = [.. s.Buys.Select(b => new PurchaseEvent
            {
                PurchasedAt = today.AddDays(-b.DaysAgo),
                Quantity = b.Qty,
                Brand = s.Brand,
                Size = s.Size,
                Source = PurchaseSource.Receipt,
            })],
            Signals = [.. (s.Signals ?? []).Select(x => new InventorySignal
            {
                Kind = x.Kind,
                SignaledAt = new DateTimeOffset(today.AddDays(-x.DaysAgo).ToDateTime(TimeOnly.MinValue)),
            })],
        }).ToList();
    }

    // ---- Recipes ------------------------------------------------------------

    private static RecipeIngredient MainIngredient(string name, string? matched, string? quantity = null) =>
        new() { Name = name, IsMain = true, MatchedProduct = matched, Quantity = quantity };
    private static RecipeIngredient Season(string name, string? quantity = null) =>
        new() { Name = name, IsMain = false, Quantity = quantity };
    private static RecipeStep Step(int order, string text) => new() { Order = order, Text = text };

    private static List<Recipe> BuildOriginalRecipes() =>
    [
        new Recipe
        {
            Name = "Weeknight Chicken & Rice",
            Blurb = "A fast one-pan dinner using what's usually on hand.",
            SavedAt = DateTimeOffset.Now.AddDays(-20),
            TimesEaten = 4,
            EstimatedCaloriesPerServing = 540,
            Ingredients =
            [
                MainIngredient("Chicken breast", "Chicken Breast", "1 lb"),
                MainIngredient("White rice", "White Rice", "1 cup"),
                MainIngredient("Broccoli", "Broccoli", "2 cups"),
                Season("Garlic", "2 cloves"), Season("Soy sauce", "2 tbsp"), Season("Olive oil"),
            ],
            Steps =
            [
                Step(1, "Cook the rice per the package."),
                Step(2, "Sear the diced chicken in oil until golden, 6–7 minutes."),
                Step(3, "Add garlic and broccoli; stir-fry until tender-crisp."),
                Step(4, "Fold in the rice, splash with soy sauce, and serve."),
            ],
        },
        new Recipe
        {
            Name = "Skillet Beef Tacos",
            Blurb = "Ground beef tacos with peppers and onion.",
            SavedAt = DateTimeOffset.Now.AddDays(-12),
            TimesEaten = 2,
            EstimatedCaloriesPerServing = 610,
            Ingredients =
            [
                MainIngredient("Ground beef", "Ground Beef", "1 lb"),
                MainIngredient("Flour tortillas", "Flour Tortillas", "8"),
                MainIngredient("Bell peppers", "Bell Peppers", "2"),
                MainIngredient("Yellow onion", "Yellow Onion", "1"),
                Season("Taco seasoning", "1 packet"), Season("Shredded cheddar", "1 cup"),
            ],
            Steps =
            [
                Step(1, "Brown the beef; drain."),
                Step(2, "Add sliced peppers and onion; cook until soft."),
                Step(3, "Stir in taco seasoning and a splash of water."),
                Step(4, "Warm the tortillas and build the tacos."),
            ],
        },
        new Recipe
        {
            Name = "Spaghetti Marinara",
            Blurb = "Pantry pasta for a no-shopping night.",
            SavedAt = DateTimeOffset.Now.AddDays(-6),
            TimesEaten = 1,
            EstimatedCaloriesPerServing = 480,
            Ingredients =
            [
                MainIngredient("Spaghetti", "Spaghetti", "12 oz"),
                MainIngredient("Marinara sauce", "Marinara Sauce", "1 jar"),
                Season("Parmesan"), Season("Garlic", "2 cloves"), Season("Olive oil"),
            ],
            Steps =
            [
                Step(1, "Boil the spaghetti until al dente."),
                Step(2, "Warm the marinara with garlic and a little olive oil."),
                Step(3, "Toss together and finish with parmesan."),
            ],
        },
    ];

    // An "Adapt"-style variant grouped under the chicken recipe — swaps the breast for thighs (a product
    // that's NOT stocked), so it shows the variant grouping + the ?uses filter's variant handling.
    private static Recipe BuildChickenThighVariant(int parentId) => new()
    {
        Name = "Weeknight Chicken Thighs & Rice",
        Blurb = "Adapted to use chicken thighs — richer, a touch longer to cook.",
        SavedAt = DateTimeOffset.Now.AddDays(-3),
        ParentRecipeId = parentId,
        EstimatedCaloriesPerServing = 600,
        Ingredients =
        [
            MainIngredient("Chicken thighs", "Chicken Thighs", "1.25 lb"),
            MainIngredient("White rice", "White Rice", "1 cup"),
            MainIngredient("Broccoli", "Broccoli", "2 cups"),
            Season("Garlic", "2 cloves"), Season("Soy sauce", "2 tbsp"), Season("Olive oil"),
        ],
        Steps =
        [
            Step(1, "Cook the rice per the package."),
            Step(2, "Sear the thighs skin-side down until crisp, 8–9 minutes, then flip."),
            Step(3, "Add garlic and broccoli; stir-fry until tender-crisp."),
            Step(4, "Slice the thighs, fold in the rice with soy sauce, and serve."),
        ],
    };
}
