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
/// Every purchase also rides on a synthetic CONFIRMED "trip" receipt with a priced line, because all cost
/// surfaces (grocery-list estimates, Trends, price history) read prices from confirmed receipt lines —
/// purchases alone would show $0 everywhere. Two items carry a deliberate price trend for the Trends page:
/// coffee has been creeping UP, eggs are easing back down.
///
/// Guarded: it only seeds an EMPTY catalog, so it can never clobber real data.
/// </summary>
public sealed class DemoDataSeeder(IHouseholdDbFactory dbFactory)
{
    public sealed record Result(bool Seeded, int Products, int Purchases, int Recipes, string Message);

    public async Task<Result> SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Products.AnyAsync(ct))
            return new Result(false, 0, 0, 0, "Sample data skipped — the catalog already has items.");

        var (products, receipts) = BuildCatalog(DateOnly.FromDateTime(DateTime.Today));
        db.Products.AddRange(products);
        db.Receipts.AddRange(receipts);

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

        // A dated meal log matching each recipe's TimesEaten, spread over recent weeks, so the demo
        // household's Reports tab (meals + calories over time) has something honest to chart.
        db.MealEvents.AddRange(BuildMealLog(originals, DateOnly.FromDateTime(DateTime.Today)));
        await db.SaveChangesAsync(ct);

        var purchases = products.Sum(p => p.Purchases.Count);
        var recipeCount = await db.Recipes.CountAsync(ct);
        return new Result(true, products.Count, purchases, recipeCount,
            $"Loaded {products.Count} sample products, {purchases} purchases, and {recipeCount} recipes.");
    }

    // ---- Products -----------------------------------------------------------

    // One seeded product: its aisle, current shelf price + descriptive tags, and the trips that bought it
    // (each as days-ago + quantity), plus any live signals and "also works as" substitutes.
    // DriftPerDayAgo is the signed fraction of the price added per day in the past — NEGATIVE means it
    // used to be cheaper (the price is rising), positive means it's coming down.
    // BuyVariants rotates brand + variety across the buys (cycled by buy index) for items bought in
    // flavors — the Variety feature's demo stage; null keeps the product's one Brand and no variety.
    private sealed record Seed(
        string Name, Category Category, string? Brand, string Size, decimal Price, string[] Tags,
        (int DaysAgo, decimal Qty)[] Buys,
        (int DaysAgo, SignalKind Kind)[]? Signals = null,
        string[]? AlsoWorksAs = null,
        double DriftPerDayAgo = 0,
        (string? Brand, string? Variety)[]? BuyVariants = null,
        // v4.0 §13: the household counted this one. CountedDaysAgo is the ATTESTATION date, so it also
        // decides whether the drift check has fired — a distant count is a different demo from a fresh one.
        decimal? CountOnHand = null,
        int CountedDaysAgo = 0,
        // Product.DefaultUnit — DISPLAY only (§13.1/§13.6). A real receipt import never sets it (its one
        // writer is the manual add-product form), so a seed that sets it is showing what a household gets
        // after typing the unit in by hand. It deliberately does NOT decide the decrement: §13.3 reads the
        // fractionality of the quantities, which is why a weight hero needs fractional Buys and not this.
        string? Unit = null,
        // v3.6: the label on the LATEST buy, days from today (negative = already past). Dormant unless the
        // household turns TrackExpirationDates on, which is exactly how the feature ships.
        int? ExpiresInDays = null);

    private static (List<Product> Products, List<Receipt> Receipts) BuildCatalog(DateOnly today)
    {
        // Fixed seeds → the "messy" jitter is identical every run (reproducible demo + testable).
        var rng = new Random(20260705);
        var priceRng = new Random(20260706); // separate stream so price jitter can't shift the trip dates

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
            new("Whole Milk", Category.Dairy, "Great Value", "1 gal", 3.86m, ["Breakfast"],
                [(7, 1), (13, 1), (18, 1), (27, 1), (33, 1), (40, 1), (51, 1)]),

            // Dogs eat faster than we rebuy: OutNow keeps landing ~14 days after each 26-day rebuy, so BURN
            // RATE (14d) diverges from the rebuy rhythm (26d) and takes over the prediction.
            new("Dry Dog Food", Category.PetCare, "Pedigree", "30 lb", 24.98m, ["Dog"],
                [(8, 1), (34, 1), (62, 1), (88, 1)],
                Signals: [(20, SignalKind.OutNow), (46, SignalKind.OutNow), (74, SignalKind.OutNow)]),

            // Stock-up: the last trip bought 3× the usual, so StockUpFactor stretches the due date out
            // instead of nagging on the one-pack cadence.
            new("Paper Towels", Category.Household, "Bounty", "6 rolls", 15.97m, ["Paper"],
                [(5, 3), (26, 1), (48, 1), (70, 1)]),

            // THE HOARD (v4.0's "What's piling up" hero): a freezer-filling trip — six roasts at once,
            // on an item previously bought one at a time — and then silence, with no "out" ever
            // signalled. StockUpFactor stretches the projection sixfold (14-day rhythm → ~84 days), and
            // this is still a month and a half past even THAT, which is what makes it a finding rather
            // than an item merely bought in bulk. Deliberately NO OutNow — a household eating through a
            // hoard has nothing to report. Its dates are load-bearing: shorten the silence and the
            // grocery list is right to ask again.
            new("Beef Chuck Roast", Category.Meat, null, "3 lb", 18.94m, ["Protein", "Freezer"],
                [(130, 6), (144, 1), (158, 1), (172, 1), (186, 1)]),

            // COUNTED (v4.0 §13's hero, and the answer the hoard above can't give): bought on a ~21-day
            // rhythm and now well past due, so §6 alone would be asking for more — except the household
            // actually counted five on the shelf three days ago. Every buying surface defers to that and
            // says WHY ("You have 5, counted …") rather than silently dropping the row. It exists
            // because the catalog otherwise demonstrated the counting feature nowhere: the backlog check
            // NAMES items worth counting, and nothing showed what happens once you do.
            // Fresh on purpose — five packages on a 21-day rhythm are months from the drift check, so
            // this reads as suppression working rather than as a stale count.
            new("Canned Black Beans", Category.Pantry, "Great Value", "15 oz", 1.12m, ["Pantry"],
                [(30, 1), (52, 1), (73, 1), (95, 1)], CountOnHand: 5, CountedDaysAgo: 3),

            // A COUNT GONE STALE (§13.5's drift check): counted 3 a hundred and ten days ago on a ~14-day
            // rhythm, so three should have been gone about 68 days back. The engine stops trusting the
            // number and asks instead of silently correcting it — the answer to "an inventory decays".
            // This state CANNOT be produced through the UI: every write path stamps the attestation date
            // as NOW, so only a seed (or waiting three months) can show it. That's the reason it exists.
            new("Canned Diced Tomatoes", Category.Pantry, "Hunt's", "14.5 oz", 1.28m, ["Pantry"],
                [(120, 1), (134, 1), (148, 1), (163, 1)], CountOnHand: 3, CountedDaysAgo: 110),

            // A WEIGHT ITEM (§13.3): quantities are the weight itself, which is what extraction writes for
            // a weight-priced line ("1.24 lb @ 5.48/lb" — prompt rule 6). The fractional median is what
            // makes "one package" 1.24 lb rather than an arbitrary 1, and the FRACTIONALITY is what tells
            // the app that: Unit is set here only so the display can say "lb", and a real receipt import
            // would leave it null. Counted at three packs' worth.
            new("Ground Chuck", Category.Meat, null, "lb", 5.48m, ["Protein"],
                [(9, 1.24m), (23, 1.18m), (38, 1.31m), (52, 1.22m)],
                Unit: "lb", CountOnHand: 3.72m, CountedDaysAgo: 2),

            // CENSUS STOCK (§13.8's output shape, available now): a quarter cow split and frozen — bought
            // from a farm, so no receipt ever saw it and there is NO purchase history at all. Its status
            // stays "Still learning" forever, which is correct and deliberate: the app was never going to
            // ask you to buy this, so there is nothing for the count to suppress. What the count DOES do is
            // tell the recipes you have beef — the only way stock like this can reach makeability, since
            // reading status alone would leave the number as decoration. Counted recently, so it reads as
            // the working state; §13.5's age fallback is what eventually asks about it (90 days).
            new("Quarter Cow Ground Beef", Category.Meat, null, "1 lb pack", 4.95m, ["Protein", "Freezer"],
                [], CountOnHand: 14, CountedDaysAgo: 20),

            // A COUNTED ITEM WITH A LABEL (§13.5's sharpest interaction). Two cartons on the shelf, a
            // metronomic 7-day rhythm putting the next buy 3 days out, and a best-by in 2 — so the label
            // lands INSIDE the rhythm's projection, which is the only arrangement where the two can be
            // seen to disagree. While expiration tracking is OFF this is just a counted item that the count
            // keeps off the list, dormant exactly as v3.6 ships. Turn the household toggle on and the LABEL
            // takes over: how many you have says nothing about whether they're still good, so suppression
            // stands down and the item reaches Due Soon BEFORE it dies rather than the day after. Also the
            // catalog's only expiration-dated purchase, so /reports' Waste watch has something to judge.
            new("Heavy Whipping Cream", Category.Dairy, "Great Value", "16 fl oz", 2.72m, ["Breakfast"],
                [(4, 1), (11, 1), (18, 1), (25, 1)],
                CountOnHand: 2, CountedDaysAgo: 1, ExpiresInDays: 2),

            // Vacation gap: one 45-day interval among ~12-day ones. MedianWithTrim drops it (> 3× median) so
            // the cadence stays honest at ~12 days. Also the Trends "price is climbing" hero: ~15% cheaper
            // 100 days ago, so its ticker shows a steady red ▲.
            new("Ground Coffee", Category.Beverage, "Folgers", "30.5 oz", 13.98m, ["Breakfast"],
                [(7, 1), (19, 1), (31, 1), (76, 1), (88, 1), (100, 1)], DriftPerDayAgo: -0.0015),

            // Marked out right now → pinned Overdue with a "Marked out of stock" note (signal override).
            new("Dish Soap", Category.Household, "Dawn", "19 oz", 4.79m, ["Cleaning"],
                [(30, 1), (58, 1)], Signals: [(2, SignalKind.OutNow)]),

            // Flagged running low by hand → floored to DueSoon even though the stats say Stocked.
            new("Paper Napkins", Category.Household, "Vanity Fair", "100 ct", 3.42m, ["Paper"],
                Trips(3, 40, 5, 6), Signals: [(1, SignalKind.RunningLow)]),

            // Variety hero: one item bought across two brands AND several flavors — the cadence is the
            // drink mix's collectively, while Product Detail splits the buys by variety (note strawberry
            // arrives from BOTH brands, so the variety rows really do pool across brand). Quantities
            // vary like real shopping — nobody buys exactly one packet forever.
            new("Drink Mix", Category.Beverage, "Kool-Aid", "6 ct", 3.24m, ["Snack"],
                [(3, 2), (12, 2), (21, 1), (30, 2), (38, 1), (47, 3)],
                BuyVariants: [("Kool-Aid", "Strawberry"), ("Crystal Light", "Grape"),
                              ("Kool-Aid", "Grape"), ("Crystal Light", "Strawberry"),
                              ("Kool-Aid", "Strawberry"), ("Kool-Aid", "Tropical Punch")]),

            // Unbranded varieties, bought loose and TWO KINDS PER TRIP (paired same-day lines):
            // 3 Gala + 3 Honeycrisp on one receipt is a six-apple trip, so the grocery list should
            // say 6 with "Gala +2" — the per-trip quantity + variety-hint case, exactly as shopped.
            new("Apples", Category.Produce, null, "each", 0.78m, ["Fruit", "Snack"],
                [(4, 3), (4, 3), (13, 3), (13, 3), (23, 3), (23, 2), (32, 3), (32, 3)],
                BuyVariants: [(null, "Gala"), (null, "Honeycrisp"), (null, "Gala"), (null, "Honeycrisp"),
                              (null, "Gala"), (null, "Fuji"), (null, "Gala"), (null, "Honeycrisp")]),

            // ---- Overdue by the stats (populate the dashboard's "overdue") ----
            new("Sandwich Bread", Category.Pantry, "Nature's Own", "20 oz", 2.98m, ["Bakery"],
                [(11, 1), (18, 1), (26, 1), (33, 1), (40, 1)]),
            new("Bananas", Category.Produce, null, "bunch", 1.52m, ["Fruit", "Snack"],
                [(6, 1), (11, 1), (17, 1), (22, 1), (28, 1)]),

            // ---- Recipe-backing staples, kept in stock so the saved recipes read "Ready to make" ----
            new("Chicken Breast", Category.Meat, "Tyson", "2.5 lb", 12.97m, ["Protein"], Trips(5, 12, 3, 3),
                AlsoWorksAs: ["chicken", "chicken cutlet"]),
            new("White Rice", Category.Pantry, "Great Value", "5 lb", 3.98m, ["Grain"], Trips(4, 30, 6, 8)),
            new("Broccoli", Category.Produce, null, "12 oz", 2.18m, ["Vegetable"], Trips(5, 9, 2, 2)),
            new("Ground Beef", Category.Meat, null, "1 lb", 5.48m, ["Protein"], Trips(6, 10, 3, 4),
                AlsoWorksAs: ["ground meat"]),
            new("Yellow Onion", Category.Produce, null, "3 lb bag", 2.68m, ["Vegetable"], Trips(4, 24, 5, 5)),
            new("Bell Peppers", Category.Produce, null, "3 ct", 3.24m, ["Vegetable"], Trips(5, 11, 3, 4)),
            new("Flour Tortillas", Category.Pantry, "Mission", "8 ct", 2.78m, ["Bakery"], Trips(4, 16, 4, 6)),
            new("Shredded Cheddar", Category.Dairy, "Great Value", "8 oz", 2.42m, ["Cheese"], Trips(5, 13, 3, 5)),

            // ---- Background catalog: varied cadences + jitter for a healthy status spread ----
            // Eggs are the "price is easing" counter-hero: pricier in the past, drifting down (green ▼).
            new("Large Eggs", Category.Dairy, "Great Value", "18 ct", 4.86m, ["Breakfast", "Protein"],
                Trips(6, 9, 2, 8), DriftPerDayAgo: 0.002),
            new("Greek Yogurt", Category.Dairy, "Chobani", "32 oz", 5.94m, ["Breakfast"], Trips(5, 11, 3, 9),
                BuyVariants: [("Chobani", "Strawberry"), ("Chobani", "Plain"), ("Chobani", "Blueberry"),
                              ("Chobani", "Plain"), ("Chobani", "Strawberry")]),
            new("Salted Butter", Category.Dairy, "Land O'Lakes", "1 lb", 4.48m, ["Baking"], Trips(4, 26, 5, 4)),
            new("Bacon", Category.Meat, "Oscar Mayer", "16 oz", 6.48m, ["Protein", "Breakfast"], Trips(4, 18, 4, 15)),
            new("Baby Spinach", Category.Produce, null, "10 oz", 2.98m, ["Vegetable", "Salad"], Trips(5, 8, 2, 7)),
            new("Roma Tomatoes", Category.Produce, null, "1 lb", 1.86m, ["Vegetable"], Trips(5, 9, 3, 3)),
            new("Spaghetti", Category.Pantry, "Barilla", "16 oz", 1.92m, ["Grain"], Trips(4, 21, 5, 6)),
            new("Marinara Sauce", Category.Pantry, "Rao's", "24 oz", 7.48m, ["Canned"], Trips(4, 22, 5, 4)),
            new("Peanut Butter", Category.Pantry, "Jif", "40 oz", 6.72m, ["Snack"], Trips(3, 34, 6, 12)),
            new("Breakfast Cereal", Category.Pantry, "General Mills", "18 oz", 4.12m, ["Breakfast"], Trips(6, 7, 2, 5)),
            new("Tortilla Chips", Category.Pantry, "Tostitos", "13 oz", 3.98m, ["Snack"], Trips(5, 10, 3, 9)),
            new("Orange Juice", Category.Beverage, "Simply", "52 oz", 3.68m, ["Breakfast"], Trips(5, 12, 3, 11)),
            new("Frozen Pizza", Category.Frozen, "DiGiorno", "1 ct", 6.86m, ["Dinner"], Trips(4, 15, 4, 3)),
            new("Frozen Blueberries", Category.Frozen, "Great Value", "16 oz", 3.24m, ["Fruit", "Breakfast"], Trips(3, 24, 6, 20)),
            new("Toilet Paper", Category.Household, "Charmin", "12 rolls", 12.28m, ["Paper"], Trips(4, 24, 4, 18)),
            new("Hand Soap", Category.PersonalCare, "Softsoap", "11 oz", 1.98m, ["Bath"], Trips(3, 30, 6, 10)),
            new("Toothpaste", Category.PersonalCare, "Colgate", "6 oz", 3.48m, ["Bath"], Trips(3, 40, 7, 26)),
            new("Cat Litter", Category.PetCare, "Fresh Step", "20 lb", 9.48m, ["Cat"], Trips(4, 17, 4, 12)),

            // ---- "Still learning": one buy, so cadence is honestly Unknown ----
            new("Sriracha Sauce", Category.Pantry, "Huy Fong", "17 oz", 4.28m, ["Condiment"], [(14, 1)]),
            new("Olive Oil", Category.Pantry, "Bertolli", "25 oz", 8.97m, ["Oil"], [(9, 1)]),
        };

        // Per-buy price: the current shelf price drifted back in time (see Seed.DriftPerDayAgo), plus a
        // ±3% trip-to-trip wiggle so the tickers and price-history charts look like real shelves.
        decimal PriceOn(Seed s, int daysAgo)
        {
            var drifted = (double)s.Price * (1 + s.DriftPerDayAgo * daysAgo);
            var jittered = drifted * (1 + (priceRng.NextDouble() * 2 - 1) * 0.03);
            return Math.Round((decimal)jittered, 2);
        }

        // One synthetic "shopping trip" receipt per calendar day with a purchase. Every cost surface —
        // grocery-list estimates, Trends, the price-history chart — prices from confirmed ReceiptLines,
        // so purchases without lines would show $0 everywhere. Confirmed receipts are never rendered or
        // re-extracted (only PendingReview ones are), so the placeholder ImagePath — required by the
        // entity, backed by no file — is never resolved.
        var trips = new Dictionary<DateOnly, Receipt>();
        Receipt TripOn(DateOnly date)
        {
            if (!trips.TryGetValue(date, out var receipt))
                trips[date] = receipt = new Receipt
                {
                    Merchant = "Sample Market",
                    PurchasedAt = date,
                    ImagePath = "demo/no-image",
                    Status = ReceiptStatus.Confirmed,
                };
            return receipt;
        }

        var products = new List<Product>();
        foreach (var s in seeds)
        {
            var product = new Product
            {
                Name = s.Name,
                Category = s.Category,
                IsTracked = true,
                DefaultUnit = s.Unit,
                TrackQuantity = s.CountOnHand is not null,
                QuantityOnHand = s.CountOnHand,
                QuantityCountedAt = s.CountOnHand is null
                    ? null
                    : new DateTimeOffset(today.AddDays(-s.CountedDaysAgo).ToDateTime(TimeOnly.MinValue)),
                Tags = [.. s.Tags.Select(t => new ProductTag { Value = t })],
                Substitutes = [.. (s.AlsoWorksAs ?? []).Select(v => new ProductSubstitute { Value = v })],
                Signals = [.. (s.Signals ?? []).Select(x => new InventorySignal
                {
                    Kind = x.Kind,
                    SignaledAt = new DateTimeOffset(today.AddDays(-x.DaysAgo).ToDateTime(TimeOnly.MinValue)),
                })],
            };

            // Only the LATEST purchase's label governs (v3.6), so the seed stamps exactly that one. Found
            // by the minimum days-ago rather than index 0, so a seed listing its buys in any order still
            // dates the right purchase.
            var latestDaysAgo = s.Buys.Length > 0 ? s.Buys.Min(b => b.DaysAgo) : 0;

            for (var buy = 0; buy < s.Buys.Length; buy++)
            {
                var (daysAgo, qty) = s.Buys[buy];
                DateOnly? expires = s.ExpiresInDays is { } days && daysAgo == latestDaysAgo
                    ? today.AddDays(days)
                    : null;
                // Items sold in flavors rotate brand + variety across their buys; the rest keep the
                // product's single brand. The raw line carries the variety like a real shelf label.
                var (brand, variety) = s.BuyVariants is { Length: > 0 } variants
                    ? variants[buy % variants.Length]
                    : (s.Brand, null);
                var date = today.AddDays(-daysAgo);
                var trip = TripOn(date);
                trip.Lines.Add(new ReceiptLine
                {
                    RawText = string.Join(' ',
                        new[] { brand, variety, s.Name, s.Size }.Where(v => !string.IsNullOrEmpty(v))).ToUpperInvariant(),
                    NormalizedName = s.Name,
                    Brand = brand,
                    Size = s.Size,
                    Variety = variety,
                    Quantity = qty,
                    UnitPrice = PriceOn(s, daysAgo),
                    ExpirationDate = expires,
                    Category = s.Category,
                    Confidence = 1,
                    Product = product,
                });
                product.Purchases.Add(new PurchaseEvent
                {
                    PurchasedAt = date,
                    Quantity = qty,
                    Brand = brand,
                    Size = s.Size,
                    Variety = variety,
                    ExpirationDate = expires,
                    Source = PurchaseSource.Receipt,
                    Receipt = trip, // tie the buy to its trip so per-purchase price lookups hit exactly
                });
            }

            products.Add(product);
        }

        return (products, [.. trips.Values.OrderBy(r => r.PurchasedAt)]);
    }

    // ---- Recipes ------------------------------------------------------------

    /// <summary>One MealEvent per TimesEaten tap, walking back from a few days ago at a roughly weekly
    /// rhythm per recipe (staggered per recipe so meals don't all land on the same weekday). Counts stay
    /// consistent with the counters on the cards — the parity a real household only has going forward.</summary>
    private static IEnumerable<MealEvent> BuildMealLog(IReadOnlyList<Recipe> recipes, DateOnly today)
    {
        for (var r = 0; r < recipes.Count; r++)
        {
            var recipe = recipes[r];
            for (var i = 0; i < recipe.TimesEaten; i++)
            {
                yield return new MealEvent
                {
                    RecipeId = recipe.Id,
                    AteAt = today.AddDays(-(2 + r * 2 + i * 7)),
                };
            }
        }
    }

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
