using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;
using ShelfAware.Core.Settings;

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
/// <para>It is also the app's TEST ENVIRONMENT, and held to that standard: a feature with no seeded
/// instance is a feature nobody can look at. Several states here exist for no other reason and cannot be
/// produced through the UI at all — a count that has drifted stale (every write stamps the attestation as
/// NOW), a household that stopped counting but kept the number, a pre-variety product still carrying its
/// flavor in its name. Where a state genuinely can't be seeded honestly it is named as such rather than
/// faked: there is no seeded AI usage (it would misreport what a household spent) and no deliberately
/// misread quantity (a demo must not ship known-wrong data to show off the tool that repairs it).</para>
///
/// Guarded: it only seeds an EMPTY catalog, so it can never clobber real data.
/// </summary>
public sealed class DemoDataSeeder(
    IHouseholdDbFactory dbFactory, ReceiptStorage storage, ILogger<DemoDataSeeder> logger)
{
    public sealed record Result(bool Seeded, int Products, int Purchases, int Recipes, string Message);

    public async Task<Result> SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Products.AnyAsync(ct))
            return new Result(false, 0, 0, 0, "Sample data skipped — the catalog already has items.");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var (products, receipts) = BuildCatalog(today);
        db.Products.AddRange(products);
        db.Receipts.AddRange(receipts);

        db.ExcludedFoods.AddRange(
            new ExcludedFood { Value = "mushrooms" },
            new ExcludedFood { Value = "cilantro" });
        db.GroceryExtras.AddRange(
            new GroceryExtra { Name = "Aluminium foil" },
            new GroceryExtra { Name = "Birthday candles" });

        // The sample household tracks expiration dates. It ships OFF for a real household — it's the
        // most ritual-heavy field in the app, so people opt in — but a sample pantry that leaves it off
        // renders NONE of the feature: no panel, no "capped by the expiration date", no expired pin, and
        // a Waste watch with nothing in it. Turning it on here is what makes the seeded labels mean
        // anything, and the load message says so, because a setting that changed itself silently is
        // worse than a feature nobody found.
        //
        // ⚠️ Updated if present, not blind-inserted. The seed guard is about the CATALOG, so a household
        // with an empty pantry can already hold settings rows — the Settings page writes this exact key
        // the moment anyone touches the toggle, and the sample-data button is on offer the whole time.
        // Adding a second row collides on the composite key (HouseholdId, Key) and takes the entire seed
        // down, in the one flow this button has. Same read-then-write shape as EfAppSettings.SetAsync.
        if (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.TrackExpirationDates, ct) is { } existing)
            existing.Value = "true";
        else
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.TrackExpirationDates, Value = "true" });

        var originals = BuildOriginalRecipes();
        db.Recipes.AddRange(originals);
        await db.SaveChangesAsync(ct); // populate recipe Ids so the variant can point at its parent

        var chicken = originals.First(r => r.Name.Contains("Chicken", StringComparison.OrdinalIgnoreCase));
        db.Recipes.Add(BuildChickenThighVariant(chicken.Id));

        // A dated meal log matching each recipe's TimesEaten, spread over recent weeks, so the demo
        // household's Reports tab (meals + calories over time) has something honest to chart.
        db.MealEvents.AddRange(BuildMealLog(originals, today));

        // Everything below needs the ids the save above assigned.
        db.ProductAliases.AddRange(BuildAliases(products, receipts));
        db.SavedReports.AddRange(BuildSavedReports(today));
        StampProductOrigins(products, receipts);
        db.Receipts.Add(await BuildPendingReceiptAsync(ct));
        await db.SaveChangesAsync(ct);

        var purchases = products.Sum(p => p.Purchases.Count);
        var recipeCount = await db.Recipes.CountAsync(ct);
        return new Result(true, products.Count, purchases, recipeCount,
            $"Loaded {products.Count} sample products, {purchases} purchases, and {recipeCount} recipes. " +
            "Expiration tracking is on for this pantry — turn it off any time in Settings.");
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
        // Sizes rotated across the buys (cycled by buy index), for an item the household buys in more
        // than one package size — milk as a gallon OR a half-gallon, the case the data model's
        // size-is-metadata decision exists for. Null keeps the product's single Size on every buy.
        // The predictor's two branches are BOTH worth seeding and they're chosen by the data: a size
        // bought ≥2 times drives the cadence alone (dominant), and a product with no size bought twice
        // falls back to all purchases. See the two products that use this.
        string[]? BuySizes = null,
        // v4.0 §13: the household counted this one. CountedDaysAgo is the ATTESTATION date, so it also
        // decides whether the drift check has fired — a distant count is a different demo from a fresh one.
        decimal? CountOnHand = null,
        int CountedDaysAgo = 0,
        // v4.1: counting was turned OFF again, but the number and its date are KEPT. "Off is dormant,
        // not destructive" — every reader gates on TrackQuantity, so the pair influences nothing and
        // the product page attributes it ("you counted 2 on <date>") instead of amnesia. Unreachable
        // from a seed without this flag, since CountOnHand alone turns tracking on.
        bool CountDormant = false,
        // Product.IsTracked. False = the household stopped following this item: it leaves the
        // dashboard, the grocery list and recipe stock, and shows only under the Products grid's
        // untracked filter. Everything ships tracked, so without a seed that filter matches nothing.
        bool Tracked = true,
        // Purchases with no receipt behind them — the "Bought today" button (Manual) and the chat's
        // add_purchase (Chat). They carry no ReceiptLine and no ReceiptId, which is exactly what
        // makes them matter to receipt removal: a product with history from another source survives
        // the removal of the receipt that introduced it.
        (int DaysAgo, decimal Qty, PurchaseSource Source)[]? OffReceiptBuys = null,
        // Product.DefaultUnit — DISPLAY only (§13.1/§13.6). A real receipt import never sets it (its one
        // writer is the manual add-product form), so a seed that sets it is showing what a household gets
        // after typing the unit in by hand. It deliberately does NOT decide the decrement: §13.3 reads the
        // fractionality of the quantities, which is why a weight hero needs fractional Buys and not this.
        string? Unit = null,
        // v3.6 labels: (which buy, when it expires) — both in days from TODAY, so ExpiresInDays is
        // NEGATIVE for a date that has already passed. Per-purchase like Brand/Size/Variety, and
        // deliberately not limited to the latest buy: only the latest one governs the PREDICTION, but
        // Waste watch judges every dated purchase it can find, and it can only demonstrate its four
        // verdicts on labels that are already in the past.
        (int BuyDaysAgo, int ExpiresInDays)[]? Labels = null);

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
            // Also the SUPERSEDED half of Waste watch: the jug bought 33 days ago was labeled for 25
            // days ago and another was bought at 27 — replaced in time, no waste to suspect. A label on
            // a non-latest buy is invisible to the prediction (only the latest governs) and visible to
            // the report, which is the distinction between the two features.
            new("Whole Milk", Category.Dairy, "Great Value", "1 gal", 3.86m, ["Breakfast"],
                [(7, 1), (13, 1), (18, 1), (27, 1), (33, 1), (40, 1), (51, 1)],
                Labels: [(33, -25)]),

            // Dogs eat faster than we rebuy: OutNow keeps landing ~14 days after each 26-day rebuy, so BURN
            // RATE (14d) diverges from the rebuy rhythm (26d) and takes over the prediction.
            // Waste watch's MARKED OUT verdict rides on the same signals: the bag bought 34 days ago was
            // labeled for 15 days ago and the household said they were out at 20 — finished, not lost.
            new("Dry Dog Food", Category.PetCare, "Pedigree", "30 lb", 24.98m, ["Dog"],
                [(8, 1), (34, 1), (62, 1), (88, 1)],
                Signals: [(20, SignalKind.OutNow), (46, SignalKind.OutNow), (74, SignalKind.OutNow)],
                Labels: [(34, -15)]),

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

            // THE CONFIDENCE BAND (§13.5): the same census shape, counted 140 days ago. No rhythm means no
            // exhaustion date to be past, so the app can't say how many are LEFT — only that it has stopped
            // believing the number. So the page attributes rather than asserts: "you counted 9 on <date>"
            // instead of "9 on hand", which is still true when the second sentence has become a lie. That
            // is deliberately NOT a guess at a smaller number: without a consumption rate, elapsed time
            // says nothing about how much got eaten, and inventing a band would be the confident lie the
            // whole feature avoids. Pairs with the Quarter Cow above — same shape, opposite confidence.
            new("Home-Canned Tomato Sauce", Category.Pantry, null, "quart jar", 3.25m, ["Pantry"],
                [], CountOnHand: 9, CountedDaysAgo: 140),

            // A COUNTED ITEM WITH A LABEL (§13.5's sharpest interaction). Two cartons on the shelf, a
            // metronomic 7-day rhythm putting the next buy 3 days out, and a best-by in 2 — so the label
            // lands INSIDE the rhythm's projection, which is the only arrangement where the two can be
            // seen to disagree. Turn the household toggle OFF and this is just a counted item the count
            // keeps off the list, dormant exactly as v3.6 ships; on — which is how the sample pantry
            // loads — the LABEL takes over: how many you have says nothing about whether they're still
            // good, so suppression stands down and the item reaches Due Soon BEFORE it dies rather than
            // the day after. Its label is the catalog's only one still AHEAD, which is the one verdict
            // Waste watch can't reach on a passed date: it reports this as "not due yet" and judges
            // nothing. The four real verdicts live on the past labels seeded below and above.
            new("Heavy Whipping Cream", Category.Dairy, "Great Value", "16 fl oz", 2.72m, ["Breakfast"],
                [(4, 1), (11, 1), (18, 1), (25, 1)],
                CountOnHand: 2, CountedDaysAgo: 1, Labels: [(4, 2)]),

            // EXPIRED (v3.6's pin, and Waste watch's only real finding): the latest bunch was labeled for
            // two days ago and nothing says it was finished — no rebuy since, no "we're out", no restock.
            // So the engine pins it Overdue with the LABEL as the due date and says "expired", which is a
            // different sentence from "marked out" on purpose: the human can see it in the fridge, and a
            // state that explains itself is the difference between trusted and gaslighting. It is also
            // the one purchase Waste watch reports as PASSED QUIETLY — the "worth checking, $ at stake"
            // bucket that is the whole point of the panel, and that no other seeded row could reach.
            new("Baby Spinach", Category.Produce, null, "10 oz", 2.98m, ["Vegetable", "Salad"],
                [(7, 1), (15, 1), (23, 1), (31, 1), (39, 1)], Labels: [(7, -2)]),

            // THE OVERRIDE ("I froze it — it's fine"), the other half of the label rule and the only
            // SignalKind.Restocked that changes an expiration outcome. The pack bought 15 days ago was
            // labeled for 5 days ago; a Restocked dated AFTER the label stands the whole thing down —
            // pin AND cap, because half an override would be a lie — and the panel SAYS "overridden"
            // rather than silently not firing. Waste watch reads the same evidence and returns
            // OVERRIDDEN. A Restocked on or BEFORE the label would NOT do this: that's just "I have it".
            new("Bacon", Category.Meat, "Oscar Mayer", "16 oz", 6.48m, ["Protein", "Breakfast"],
                [(15, 1), (33, 1), (51, 1), (69, 1)],
                Signals: [(3, SignalKind.Restocked)], Labels: [(15, -5)]),

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
            // The counted RECIPE MAIN — what makes "Ate it" actually move something. Every other counted
            // hero answers a buying question; this one answers the cooking question, and without it the
            // decrement had nothing to decrement in the whole catalog: a tap reported "nothing to take"
            // because no counted product was any recipe main's grounded match. Four bags on a ~30-day
            // rhythm, so it is nowhere near due and the count suppresses nothing — it exists purely so
            // cooking Chicken & Rice takes one off, says so, and offers Undo.
            new("White Rice", Category.Pantry, "Great Value", "5 lb", 3.98m, ["Grain"], Trips(4, 30, 6, 8),
                CountOnHand: 4, CountedDaysAgo: 5),
            new("Broccoli", Category.Produce, null, "12 oz", 2.18m, ["Vegetable"], Trips(5, 9, 2, 2)),
            new("Ground Beef", Category.Meat, null, "1 lb", 5.48m, ["Protein"], Trips(6, 10, 3, 4),
                AlsoWorksAs: ["ground meat"]),
            new("Yellow Onion", Category.Produce, null, "3 lb bag", 2.68m, ["Vegetable"], Trips(4, 24, 5, 5)),
            new("Bell Peppers", Category.Produce, null, "3 ct", 3.24m, ["Vegetable"], Trips(5, 11, 3, 4)),
            new("Flour Tortillas", Category.Pantry, "Mission", "8 ct", 2.78m, ["Bakery"], Trips(4, 16, 4, 6)),
            // …and the other way a purchase arrives without paperwork: the dashboard card's "Bought
            // today" button, which records a Manual buy on the spot. Between this and Hand Soap's chat
            // buy, all three PurchaseSources are represented — they look identical to the engine and
            // very different to receipt removal.
            new("Shredded Cheddar", Category.Dairy, "Great Value", "8 oz", 2.42m, ["Cheese"], Trips(5, 13, 3, 5),
                OffReceiptBuys: [(1, 1, PurchaseSource.Manual)]),

            // ---- Background catalog: varied cadences + jitter for a healthy status spread ----
            // Eggs are the "price is easing" counter-hero: pricier in the past, drifting down (green ▼).
            new("Large Eggs", Category.Dairy, "Great Value", "18 ct", 4.86m, ["Breakfast", "Protein"],
                Trips(6, 9, 2, 8), DriftPerDayAgo: 0.002),
            new("Greek Yogurt", Category.Dairy, "Chobani", "32 oz", 5.94m, ["Breakfast"], Trips(5, 11, 3, 9),
                BuyVariants: [("Chobani", "Strawberry"), ("Chobani", "Plain"), ("Chobani", "Blueberry"),
                              ("Chobani", "Plain"), ("Chobani", "Strawberry")]),
            new("Salted Butter", Category.Dairy, "Land O'Lakes", "1 lb", 4.48m, ["Baking"], Trips(4, 26, 5, 4)),
            new("Roma Tomatoes", Category.Produce, null, "1 lb", 1.86m, ["Vegetable"], Trips(5, 9, 3, 3)),
            new("Spaghetti", Category.Pantry, "Barilla", "16 oz", 1.92m, ["Grain"], Trips(4, 21, 5, 6)),
            new("Marinara Sauce", Category.Pantry, "Rao's", "24 oz", 7.48m, ["Canned"], Trips(4, 22, 5, 4)),

            // MIXED SIZES, branch 2 of 2 — no size bought twice, so the rebuy rhythm falls back to ALL
            // purchases rather than learning from one bucket's single date. Sizes are metadata, never
            // identity: three jar sizes are one peanut butter, and there is deliberately no unit
            // arithmetic saying 40 oz is 2.5 × 16 oz.
            new("Peanut Butter", Category.Pantry, "Great Value", "40 oz", 6.72m, ["Snack"],
                Trips(3, 34, 6, 12), BuySizes: ["40 oz", "28 oz", "16 oz"]),
            new("Breakfast Cereal", Category.Pantry, "General Mills", "18 oz", 4.12m, ["Breakfast"], Trips(6, 7, 2, 5)),
            new("Tortilla Chips", Category.Pantry, "Tostitos", "13 oz", 3.98m, ["Snack"], Trips(5, 10, 3, 9)),
            // MIXED SIZES, branch 1 of 2 — the carton comes in two sizes and the household grabs
            // whichever. 52 oz is bought three times, so it's the DOMINANT bucket and the cadence is
            // learned from its dates alone (the 89 oz buys still count toward "bought N×", and 52 oz is
            // what gets recommended). One product, one rhythm, one recommended size — never "buy a
            // gallon AND a half-gallon".
            new("Orange Juice", Category.Beverage, "Simply", "52 oz", 3.68m, ["Breakfast"], Trips(5, 12, 3, 11),
                BuySizes: ["52 oz", "89 oz", "52 oz", "89 oz", "52 oz"]),
            new("Frozen Pizza", Category.Frozen, "DiGiorno", "1 ct", 6.86m, ["Dinner"], Trips(4, 15, 4, 3)),
            new("Frozen Blueberries", Category.Frozen, "Great Value", "16 oz", 3.24m, ["Fruit", "Breakfast"], Trips(3, 24, 6, 20)),
            // RAN OUT, THEN FOUND MORE — the stock-back that isn't a purchase. The out is retired by the
            // restock (any signal older than the last stock-back is no longer in effect), and the due
            // date now runs from the day it came back rather than the day it was bought. What it must
            // NOT do is teach the rhythm: "count it if I bought one, not if I found one", so the rebuy
            // rhythm still comes only from the four real buys.
            new("Toilet Paper", Category.Household, "Charmin", "12 rolls", 12.28m, ["Paper"], Trips(4, 24, 4, 18),
                Signals: [(12, SignalKind.OutNow), (10, SignalKind.Restocked)]),
            // A purchase with NO receipt behind it: someone told the assistant "grabbed hand soap" on
            // the way home. It feeds the cadence exactly like a scanned one — the rhythm is about
            // buying, not about paperwork — and it is what makes receipt removal's "this product has
            // other history, so it stays" branch reachable in the sample data.
            new("Hand Soap", Category.PersonalCare, "Softsoap", "11 oz", 1.98m, ["Bath"], Trips(3, 30, 6, 10),
                OffReceiptBuys: [(3, 1, PurchaseSource.Chat)]),
            new("Toothpaste", Category.PersonalCare, "Colgate", "6 oz", 3.48m, ["Bath"], Trips(3, 40, 7, 26)),

            // COUNTING TURNED BACK OFF (v4.1). The household counted two jugs a month ago and then
            // stopped keeping the tally — so the number and its date are still here, influencing
            // nothing, and the product page attributes it to the day it was taken instead of pretending
            // it was never counted. Dormant, not destructive: turning counting back on finds the pair
            // waiting. The only way to see it, since stopping counting through the UI keeps the number
            // but is indistinguishable from never having counted unless something seeded the history.
            new("Cat Litter", Category.PetCare, "Fresh Step", "20 lb", 9.48m, ["Cat"], Trips(4, 17, 4, 12),
                CountOnHand: 2, CountedDaysAgo: 30, CountDormant: true),

            // ---- "Still learning": one buy, so cadence is honestly Unknown ----
            new("Sriracha Sauce", Category.Pantry, "Huy Fong", "17 oz", 4.28m, ["Condiment"], [(14, 1)]),
            new("Olive Oil", Category.Pantry, "Bertolli", "25 oz", 8.97m, ["Oil"], [(9, 1)]),

            // ---- States a catalog of well-behaved staples can't otherwise show ----

            // STOPPED TRACKING: bought for the holidays, never again. It drops out of the dashboard, the
            // grocery list and recipe stock, and appears only under the Products grid's untracked
            // filter — which matched nothing at all before this row existed. Buying it again would
            // start tracking it back up (the review screen says so).
            new("Sparkling Cider", Category.Beverage, "Martinelli's", "25.4 oz", 3.98m, ["Drink"],
                [(196, 2), (203, 1)], Tracked: false),

            // A MERGE CANDIDATE — the repair path for history, and the one thing the Variety feature
            // can't fix on its own. Before v3.5 a flavor lived in the product NAME, so the same drink
            // mix split into a product per flavor and each learned half a rhythm. These two old buys
            // are that shape: ⇆ Merge on Product Detail rolls them into "Drink Mix", stamping the
            // moved purchases with the variety the name was carrying. Without a split product seeded,
            // the merge panel has nothing to offer and the feature is invisible.
            new("Strawberry Drink Mix", Category.Beverage, "Kool-Aid", "6 ct", 3.18m, ["Snack"],
                [(88, 1), (112, 2)]),

            // The tenth aisle. Category.Other is the extraction prompt's fallback for something that
            // isn't a grocery aisle at all, and it drives where the item lands when you walk the store,
            // so the grocery list should be seen to order it.
            new("Charcoal", Category.Other, "Kingsford", "16 lb", 11.97m, ["Grill"], Trips(3, 45, 8, 22)),
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
        //
        // Two merchants, because one is a special case pretending to be the general one: aliases are
        // keyed (household, MERCHANT, raw text), so a single-store catalog can never show that the same
        // shorthand means different things in different shops, and every by-merchant report has one bar.
        // Assigned by day-number so it's deterministic and a household's trips alternate the way real
        // ones do rather than splitting neatly down the middle.
        var trips = new Dictionary<DateOnly, Receipt>();
        Receipt TripOn(DateOnly date)
        {
            if (!trips.TryGetValue(date, out var receipt))
                trips[date] = receipt = new Receipt
                {
                    Merchant = date.DayNumber % 5 == 0 ? "Corner Grocery" : "Sample Market",
                    PurchasedAt = date,
                    ImagePath = "demo/no-image",
                    Status = ReceiptStatus.Confirmed,
                    // The confirm ran the evening of the trip. Distinct from PurchasedAt (the date
                    // PRINTED on the receipt) and load-bearing for removal: it's what lets a later
                    // human count outrank the confirm, so removal doesn't subtract stock a recount has
                    // already accounted for. Null would be read as "confirmed before the column
                    // existed" — true of old rows, a lie about these.
                    ConfirmedAt = new DateTimeOffset(date.ToDateTime(new TimeOnly(18, 30))),
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
                IsTracked = s.Tracked,
                DefaultUnit = s.Unit,
                // A dormant count keeps the number and its date and turns the FLAG off — that pair is
                // the whole v4.1 semantic, and it can't be expressed by clearing either half.
                TrackQuantity = s.CountOnHand is not null && !s.CountDormant,
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

            // A label is stamped on the buy it belongs to, matched by days-ago, so a seed can date an
            // OLD purchase as well as the current one. The predictor only ever reads the latest buy's
            // date; Waste watch reads them all, and its four verdicts are only demonstrable on labels
            // that have already come and gone.
            for (var buy = 0; buy < s.Buys.Length; buy++)
            {
                var (daysAgo, qty) = s.Buys[buy];
                DateOnly? expires = s.Labels?.Where(l => l.BuyDaysAgo == daysAgo)
                    .Select(l => (DateOnly?)today.AddDays(l.ExpiresInDays)).FirstOrDefault();
                // Items sold in flavors rotate brand + variety across their buys; the rest keep the
                // product's single brand. The raw line carries the variety like a real shelf label.
                var (brand, variety) = s.BuyVariants is { Length: > 0 } variants
                    ? variants[buy % variants.Length]
                    : (s.Brand, null);
                // …and an item bought in more than one package size rotates that too. Cycled by buy
                // index like the variants, so a seed reads as "these are the sizes, in this order".
                var size = s.BuySizes is { Length: > 0 } sizes ? sizes[buy % sizes.Length] : s.Size;
                var date = today.AddDays(-daysAgo);
                var trip = TripOn(date);
                trip.Lines.Add(new ReceiptLine
                {
                    RawText = string.Join(' ',
                        new[] { brand, variety, s.Name, size }.Where(v => !string.IsNullOrEmpty(v))).ToUpperInvariant(),
                    NormalizedName = s.Name,
                    Brand = brand,
                    Size = size,
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
                    Size = size,
                    Variety = variety,
                    ExpirationDate = expires,
                    Source = PurchaseSource.Receipt,
                    Receipt = trip, // tie the buy to its trip so per-purchase price lookups hit exactly
                });
            }

            // Buys that never came from a receipt — no line, no price, no ReceiptId. They feed the
            // cadence like any other purchase; what they don't do is leave paperwork, which is the
            // point of seeding them.
            foreach (var (daysAgo, qty, source) in s.OffReceiptBuys ?? [])
            {
                product.Purchases.Add(new PurchaseEvent
                {
                    PurchasedAt = today.AddDays(-daysAgo),
                    Quantity = qty,
                    Brand = s.Brand,
                    Size = s.Size,
                    Source = source,
                });
            }

            products.Add(product);
        }

        return (products, [.. trips.Values.OrderBy(r => r.PurchasedAt)]);
    }

    // ---- The one receipt nobody has reviewed yet ----------------------------

    /// <summary>
    /// A receipt sitting in the review queue, with the image it was read from. Everything else in the
    /// catalog is already confirmed, which left the app's most involved screen — the review grid, with
    /// its per-line tags, brand/size/variety/expiry edits, product matching and confirm — reachable only
    /// by uploading a real receipt with a working API key. These lines are already extracted, so review
    /// and confirm work with NO key at all, which is the state most visitors are in.
    ///
    /// <para>⚠️ The lines are the contents of the shipped image, transcribed. That is the whole point of
    /// shipping one: the audit copy a household can open must show the receipt the row describes, and
    /// "Retry" re-reads exactly this picture. Editing either without the other makes the screen lie.</para>
    ///
    /// <para>⚠️ It is also the ONE fixed-date row in a catalog that is otherwise generated relative to
    /// today, because the date is printed on the image and cannot be regenerated with it. An ageing
    /// review date is honest — it is exactly what an abandoned review looks like — and the date field on
    /// the review screen is editable precisely for this.</para>
    ///
    /// <para>No alias matches its lines, deliberately: this is the household's first receipt from this
    /// merchant, so the pre-fill falls to the model's own suggestion (seeded per line, the second rung of
    /// the trust order) and confirming it is what TEACHES the aliases — visibly, which is the better
    /// demonstration than finding them already there.</para>
    /// </summary>
    private async Task<Receipt> BuildPendingReceiptAsync(CancellationToken ct)
    {
        var imagePath = await SaveDemoReceiptImageAsync(ct);
        return new Receipt
        {
            Merchant = "Walmart Supercenter",
            PurchasedAt = new DateOnly(2026, 6, 10),
            ImagePath = imagePath,
            Status = ReceiptStatus.PendingReview,
            RawModelJson = DemoReceiptRawJson,
            Lines =
            [
                PendingLine("GV WHL MLK 1GAL", "Whole Milk", "Great Value", "1 gal", 1m, 3.27m,
                    Category.Dairy, 0.97m, ["Breakfast"]),
                // The weight-priced line (extraction rule 6): quantity is the WEIGHT and the unit goes in
                // size, so "one" of this is 2.31 lb. Also the low-confidence row — a two-line entry with
                // the price on the second is genuinely harder to read, and the grid styles it as such,
                // which nothing else in the sample data was ever going to show.
                PendingLine("BANANAS 2.31 lb @ 0.58/lb", "Bananas", null, "lb", 2.31m, 0.58m,
                    Category.Produce, 0.54m, ["Fruit"]),
                PendingLine("PED DOG FD 44LB", "Dry Dog Food", "Pedigree", "44 lb", 1m, 24.98m,
                    Category.PetCare, 0.94m, ["Dog"]),
                PendingLine("GV LG EGGS 12CT", "Large Eggs", "Great Value", "12 ct", 1m, 2.92m,
                    Category.Dairy, 0.96m, ["Breakfast", "Protein"]),
                PendingLine("FOLGERS CLSC 30.5OZ", "Ground Coffee", "Folgers", "30.5 oz", 1m, 12.97m,
                    Category.Beverage, 0.95m, ["Breakfast"]),
                PendingLine("GV PNT BTR 40OZ 2 @ 3.96", "Peanut Butter", "Great Value", "40 oz", 2m, 3.96m,
                    Category.Pantry, 0.93m, ["Snack"]),
            ],
        };
    }

    /// <summary>Writes the shipped receipt image into this household's receipt store and returns the
    /// ImagePath to file it under. A failure here costs the audit copy, not the catalog: the row still
    /// reviews and confirms, and the code that opens saved pages already treats a missing copy as an
    /// ordinary case. So it's logged and the seed carries on with an unbacked path — the same shape the
    /// confirmed trips already use.</summary>
    private async Task<string> SaveDemoReceiptImageAsync(CancellationToken ct)
    {
        try
        {
            await using var source = typeof(DemoDataSeeder).Assembly
                .GetManifestResourceStream("ShelfAware.Web.Data.demo-receipt.png")
                ?? throw new InvalidOperationException("The demo receipt image is missing from the assembly.");
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);

            var imagePath = await storage.NewFolderAsync(ct);
            await storage.WritePageAsync(imagePath, 0, buffer.ToArray(), "image/png", ct);
            return imagePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Sample data: couldn't save the demo receipt image; the review row will have no audit copy.");
            return "demo/no-image";
        }
    }

    private static ReceiptLine PendingLine(
        string rawText, string name, string? brand, string? size, decimal qty, decimal unitPrice,
        Category category, decimal confidence, string[] tags) => new()
        {
            RawText = rawText,
            NormalizedName = name,
            Brand = brand,
            Size = size,
            Quantity = qty,
            UnitPrice = unitPrice,
            Category = category,
            Confidence = confidence,
            TagsJson = JsonSerializer.Serialize(tags),
            // What the model itself judged this line to match. Kept through the queue so the review
            // screen pre-fills from it, exactly as it does for a receipt read a moment ago.
            SuggestedProduct = name,
        };

    /// <summary>The model output the receipt was built from, kept for audit like any other import.</summary>
    private const string DemoReceiptRawJson = """
        {"merchant":"Walmart Supercenter","purchase_date":"2026-06-10","lines":[
        {"raw_text":"GV WHL MLK 1GAL","normalized_name":"Whole Milk","brand":"Great Value","size":"1 gal","quantity":1,"unit_price":3.27,"category":"Dairy","tags":["Breakfast"],"confidence":0.97,"existing_product":"Whole Milk"},
        {"raw_text":"BANANAS 2.31 lb @ 0.58/lb","normalized_name":"Bananas","brand":null,"size":"lb","quantity":2.31,"unit_price":0.58,"category":"Produce","tags":["Fruit"],"confidence":0.54,"existing_product":"Bananas"},
        {"raw_text":"PED DOG FD 44LB","normalized_name":"Dry Dog Food","brand":"Pedigree","size":"44 lb","quantity":1,"unit_price":24.98,"category":"PetCare","tags":["Dog"],"confidence":0.94,"existing_product":"Dry Dog Food"},
        {"raw_text":"GV LG EGGS 12CT","normalized_name":"Large Eggs","brand":"Great Value","size":"12 ct","quantity":1,"unit_price":2.92,"category":"Dairy","tags":["Breakfast","Protein"],"confidence":0.96,"existing_product":"Large Eggs"},
        {"raw_text":"FOLGERS CLSC 30.5OZ","normalized_name":"Ground Coffee","brand":"Folgers","size":"30.5 oz","quantity":1,"unit_price":12.97,"category":"Beverage","tags":["Breakfast"],"confidence":0.95,"existing_product":"Ground Coffee"},
        {"raw_text":"GV PNT BTR 40OZ 2 @ 3.96","normalized_name":"Peanut Butter","brand":"Great Value","size":"40 oz","quantity":2,"unit_price":3.96,"category":"Pantry","tags":["Snack"],"confidence":0.93,"existing_product":"Peanut Butter"}]}
        """;

    // ---- What the confirms left behind --------------------------------------

    /// <summary>The merchant shorthands this household has already taught the app. An alias is the FIRST
    /// thing an upload consults — ahead of the model's own suggestion and ahead of fuzzy matching — so
    /// without any, the review screen's trust order can only ever be seen from its second rung down, and
    /// the 🔗 "matched a previous receipt line from this merchant" marker never appears. Keyed on the
    /// merchant as well as the text, which is why a two-merchant catalog matters.
    /// <para><see cref="ProductAlias.TaughtByReceiptId"/> names the confirm that taught it: only that
    /// receipt's removal un-teaches the pairing.</para></summary>
    private static IEnumerable<ProductAlias> BuildAliases(List<Product> products, List<Receipt> receipts)
    {
        // Named rather than derived, so the aliases land on staples whose receipt text is stable and
        // worth having learned. The cost of naming them is a coupling to the catalog above, and the
        // lookups below say so out loud: the only way either can fail is a seed edit in this same file,
        // and a bare "sequence contains no matching element" from inside a first-run flow is the worst
        // possible way to find that out.
        foreach (var name in (string[])["Whole Milk", "Dry Dog Food", "Ground Coffee", "Large Eggs", "Sandwich Bread"])
        {
            var product = products.FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidOperationException(
                    $"The demo catalog no longer seeds a product named '{name}', which the seeded aliases point at.");
            // The earliest trip that bought it — the confirm that would have taught the pairing.
            var teacher = receipts
                .Where(r => r.Lines.Any(l => l.Product == product))
                .OrderBy(r => r.PurchasedAt)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"'{name}' has no seeded receipt line, so no confirm could have taught an alias for it.");
            yield return new ProductAlias
            {
                Merchant = teacher.Merchant!,
                RawText = teacher.Lines.First(l => l.Product == product).RawText,
                ProductId = product.Id,
                TaughtByReceiptId = teacher.Id,
            };
        }
    }

    /// <summary>Two reports the household kept. The query is a <see cref="ReportSpecUrl"/> string —
    /// the same serialization a shared link uses — so a saved row is readable in the DB and survives a
    /// version that adds options. Seeded because the saved-report rail is otherwise empty until a
    /// visitor builds and names one, which is the last thing anyone does on a page full of presets.</summary>
    private static IEnumerable<SavedReport> BuildSavedReports(DateOnly today)
    {
        yield return new SavedReport
        {
            Name = "Spend by aisle",
            Query = ReportSpecUrl.ToQuery(new ReportSpec
            {
                From = today.AddDays(-180),
                To = today,
                Metric = ReportMetric.Spend,
                Grain = ReportGrain.Monthly,
                Split = ReportSplit.ByCategory,
                Chart = ReportChart.StackedBars,
            }),
            SavedAt = new DateTimeOffset(today.AddDays(-21).ToDateTime(TimeOnly.MinValue)),
        };
        yield return new SavedReport
        {
            Name = "What we actually cook",
            Query = ReportSpecUrl.ToQuery(new ReportSpec
            {
                From = today.AddDays(-90),
                To = today,
                Metric = ReportMetric.MealsCooked,
                Grain = ReportGrain.Monthly,
                Split = ReportSplit.ByRecipe,
                Chart = ReportChart.Bars,
            }),
            SavedAt = new DateTimeOffset(today.AddDays(-9).ToDateTime(TimeOnly.MinValue)),
        };
    }

    /// <summary>Point each product at the receipt whose confirm introduced it — its earliest trip. This is
    /// what "remove this receipt" reads to decide whether the product goes with it: one that the receipt
    /// introduced AND that gathered no other history since is undone too, everything else keeps its rows
    /// and just loses the breadcrumb. Unstamped, removal can only ever take the purchases back, so half
    /// the behaviour has nothing to act on. Census stock is left null on purpose — no receipt ever saw it.</summary>
    private static void StampProductOrigins(List<Product> products, List<Receipt> receipts)
    {
        foreach (var product in products)
        {
            var first = receipts
                .Where(r => r.Lines.Any(l => l.Product == product))
                .OrderBy(r => r.PurchasedAt)
                .FirstOrDefault();
            if (first is not null) product.CreatedByReceiptId = first.Id;
        }
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

    /// <param name="alternatives">Interchangeable forms for the ⇄ swap bubble-cloud, pre-cached on the
    /// ingredient exactly as the advisor would have written them (a JSON array of strings). The cloud is
    /// generated once on demand and cached forever after, so an un-cached ingredient needs an AI call to
    /// open — meaning the whole swap feature was dead on a keyless demo, which is most of them. Seeding
    /// the cache is the same move the speech cache already makes to let sample recipes talk without a
    /// key: the feature is exercised, and nobody's wallet is.</param>
    private static RecipeIngredient MainIngredient(
        string name, string? matched, string? quantity = null, string[]? alternatives = null) =>
        new()
        {
            Name = name,
            IsMain = true,
            MatchedProduct = matched,
            Quantity = quantity,
            AlternativesJson = alternatives is null ? null : JsonSerializer.Serialize(alternatives),
        };
    private static RecipeIngredient Season(string name, string? quantity = null) =>
        new() { Name = name, IsMain = false, Quantity = quantity };
    private static RecipeStep Step(int order, string text) => new() { Order = order, Text = text };
    private static RecipeTag Tag(string value) => new() { Value = value };

    private static List<Recipe> BuildOriginalRecipes() =>
    [
        new Recipe
        {
            Name = "Weeknight Chicken & Rice",
            Blurb = "A fast one-pan dinner using what's usually on hand.",
            SavedAt = DateTimeOffset.Now.AddDays(-20),
            TimesEaten = 4,
            EstimatedCaloriesPerServing = 540,
            Tags = [Tag("Dinner"), Tag("Asian"), Tag("Quick"), Tag("One-Pot")],
            Ingredients =
            [
                MainIngredient("Chicken breast", "Chicken Breast", "1 lb",
                    ["chicken thighs", "chicken tenderloins", "turkey cutlets", "pork loin"]),
                MainIngredient("White rice", "White Rice", "1 cup",
                    ["brown rice", "jasmine rice", "quinoa", "couscous"]),
                MainIngredient("Broccoli", "Broccoli", "2 cups",
                    ["green beans", "snap peas", "cauliflower", "bok choy"]),
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
            Tags = [Tag("Dinner"), Tag("Mexican"), Tag("Quick")],
            Ingredients =
            [
                MainIngredient("Ground beef", "Ground Beef", "1 lb",
                    ["ground turkey", "ground chuck", "ground pork", "shredded chicken"]),
                MainIngredient("Flour tortillas", "Flour Tortillas", "8",
                    ["corn tortillas", "taco shells", "flatbread"]),
                MainIngredient("Bell peppers", "Bell Peppers", "2",
                    ["poblano peppers", "sweet mini peppers", "zucchini"]),
                MainIngredient("Yellow onion", "Yellow Onion", "1",
                    ["white onion", "red onion", "shallots"]),
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
            Tags = [Tag("Dinner"), Tag("Italian"), Tag("Pasta"), Tag("Vegetarian")],
            Ingredients =
            [
                MainIngredient("Spaghetti", "Spaghetti", "12 oz",
                    ["linguine", "penne", "angel hair", "rigatoni"]),
                MainIngredient("Marinara sauce", "Marinara Sauce", "1 jar",
                    ["tomato basil sauce", "arrabbiata sauce", "crushed tomatoes"]),
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
        Tags = [Tag("Dinner"), Tag("Asian"), Tag("One-Pot")],
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
