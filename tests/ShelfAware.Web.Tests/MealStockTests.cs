using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Cooking takes stock off the shelf (DESIGN.md §13.3). These live here rather than being unreachable
/// inside <c>Recipes.razor</c> on purpose: this is the one path that changes a hand-maintained number
/// without being asked to, and the last logic left private to a page shipped a real bug past a fully
/// green suite.
/// </summary>
public class MealStockTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>A recipe whose MAIN ingredient is matched to <paramref name="matchedProduct"/>, plus a
    /// seasoning that is deliberately NOT main — seasonings must never move a count.</summary>
    private async Task<int> Recipe(string matchedProduct, string? seasoningMatch = null)
    {
        await using var db = _db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = "Test Dinner",
            Ingredients =
            [
                new RecipeIngredient { Name = "the main", IsMain = true, MatchedProduct = matchedProduct },
                new RecipeIngredient { Name = "a seasoning", IsMain = false, MatchedProduct = seasoningMatch },
            ],
        };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<int> Product(
        string name, bool counted, decimal? onHand, string? unit = null, params decimal[] boughtQuantities)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = Category.Meat,
            DefaultUnit = unit,
            TrackQuantity = counted,
            QuantityOnHand = onHand,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var day = new DateOnly(2026, 6, 1);
        foreach (var qty in boughtQuantities)
        {
            db.PurchaseEvents.Add(new PurchaseEvent
            {
                ProductId = product.Id,
                PurchasedAt = day,
                Quantity = qty,
                Source = PurchaseSource.Receipt,
            });
            day = day.AddDays(7);
        }
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<(IReadOnlyList<MealStock.Take> Plan, decimal? OnHandAfter)> Cook(int recipeId, int productId)
    {
        await using var db = _db.CreateDbContext();
        var recipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);
        var plan = await MealStock.PlanAsync(db, recipe);
        await MealStock.ApplyAsync(db, recipe);
        await db.SaveChangesAsync();

        await using var read = _db.CreateDbContext();
        return (plan.Takes, (await read.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_counted_item_bought_six_at_a_time_still_loses_only_one()
    {
        // The bug this test exists for: a receipt line reading "Beef Chuck Roast × 6" is one purchase OF
        // six, so a median over per-purchase quantities said "one package = 6" and cooking a single
        // dinner emptied the whole freezer — which then LIFTS suppression and puts the item straight
        // back on the grocery list, the exact opposite of what counting is for.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 6m, unit: null, 6m, 6m, 6m);
        var recipeId = await Recipe("Beef Chuck Roast");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal(1m, Assert.Single(plan).Amount);
        Assert.Equal(5m, after);
    }

    [Fact]
    public async Task A_weight_item_loses_the_pack_this_household_actually_buys()
    {
        // Not a round pound — a pound is not a unit of anything about how this household buys. NOTE the
        // null unit: DefaultUnit is null on every receipt-imported product (0 of 190 on the real dev
        // database, since the only writer is the manual add form), so if the unit were the discriminator
        // this — the case §13.3 designed the median for — would deduct an arbitrary 1. The fractional
        // quantities are what identify it.
        var productId = await Product("Ground Beef", counted: true, onHand: 5m, unit: null, 1.18m, 1.24m, 1.31m);
        var recipeId = await Recipe("Ground Beef");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal(1.24m, Assert.Single(plan).Amount);
        Assert.Equal(3.76m, after);
    }

    [Fact]
    public async Task A_misleading_unit_cannot_turn_a_counted_item_into_a_weight_one()
    {
        // The other half of dropping the unit from the decision: "each" is a real thing a human might
        // type, and it is NOT a weight. Consulting it would have charged six for cooking one — the same
        // freezer-emptying bug, arriving through the field that was supposed to prevent it.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 6m, unit: "each", 6m, 6m, 6m);
        var recipeId = await Recipe("Beef Chuck Roast");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal(1m, Assert.Single(plan).Amount);
        Assert.Equal(5m, after);
    }

    [Fact]
    public async Task The_plan_describes_exactly_what_the_write_performs()
    {
        // The confirm panel renders the plan and the tap performs the write. If they could ever differ
        // the panel would be lying, so they share one query and this pins the pairing.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 2m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");

        var (plan, after) = await Cook(recipeId, productId);

        var take = Assert.Single(plan);
        Assert.Equal("Beef Chuck Roast", take.ProductName);
        Assert.Equal(2m, take.OnHand);
        Assert.Equal(1m, take.Remaining);
        Assert.Equal(after, take.Remaining);
    }

    [Fact]
    public async Task A_count_already_at_none_needs_no_confirmation_because_nothing_would_change()
    {
        // StockLedger clamps at zero, so a take here is a provable no-op — putting a confirm step in
        // front of it is friction that buys nothing, on the most casual tap in the app.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 0m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Empty(plan);
        Assert.Equal(0m, after);
    }

    [Fact]
    public async Task A_plan_still_matching_the_shelf_is_accepted()
    {
        await using var db = _db.CreateDbContext();
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 3m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");
        var recipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);

        var shown = await MealStock.PlanAsync(db, recipe);
        await using var later = _db.CreateDbContext();
        var current = await MealStock.PlanAsync(later, recipe);

        Assert.True(MealStock.Matches(shown, current));
        await using var read = _db.CreateDbContext();
        Assert.Equal(3m, // planning is read-only — neither call may touch the shelf
            (await read.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_plan_the_shelf_moved_under_is_rejected()
    {
        // The preview and the commit are two user actions on two DbContexts with an unbounded gap: a
        // receipt confirm, a set_quantity, or a second cook can move the count in between. "The panel
        // cannot promise what the write doesn't do" only holds if the write CHECKS — and a test that
        // shares one context can never show that, which is how this shipped.
        await using var db = _db.CreateDbContext();
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 3m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");
        var recipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);

        var shown = await MealStock.PlanAsync(db, recipe);

        // Somebody else confirms a receipt for the same item while the panel sits open.
        await using (var other = _db.CreateDbContext())
        {
            var p = await other.Products.SingleAsync(x => x.Id == productId);
            p.QuantityOnHand = 8m;
            await other.SaveChangesAsync();
        }

        await using var later = _db.CreateDbContext();
        var current = await MealStock.PlanAsync(later, recipe);

        Assert.False(MealStock.Matches(shown, current));
        Assert.Equal(8m, Assert.Single(current.Takes).OnHand);
    }

    [Fact]
    public async Task A_plan_is_rejected_when_the_item_stopped_being_counted()
    {
        // The other direction: the take vanishes entirely rather than changing. A count-less plan must
        // not silently pass the equality check as "nothing changed".
        await using var db = _db.CreateDbContext();
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 3m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");
        var recipe = await db.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);

        var shown = await MealStock.PlanAsync(db, recipe);

        await using (var other = _db.CreateDbContext())
        {
            var p = await other.Products.SingleAsync(x => x.Id == productId);
            StockLedger.StopCounting(p);
            await other.SaveChangesAsync();
        }

        await using var later = _db.CreateDbContext();
        var current = await MealStock.PlanAsync(later, recipe);

        Assert.False(current.NeedsConfirmation);
        Assert.False(MealStock.Matches(shown, current));
    }

    [Fact]
    public async Task Cooking_never_writes_an_outage_even_when_it_empties_the_count()
    {
        // §13.4's load-bearing asymmetry, from the decrement side: reaching zero by arithmetic is a
        // HYPOTHESIS. Only a human's "we're out" may mint an OutNow, because that signal teaches the
        // burn-rate rhythm and a guess must never become training data.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 1m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Beef Chuck Roast");

        var (_, after) = await Cook(recipeId, productId);

        Assert.Equal(0m, after);
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync());
    }

    [Fact]
    public async Task An_uncounted_product_is_left_alone_and_needs_no_confirmation()
    {
        // An empty plan is what lets the ordinary "Ate it" stay a single tap: no counted item, no
        // preview, no friction.
        var productId = await Product("Bananas", counted: false, onHand: null, unit: null, 1m, 1m);
        var recipeId = await Recipe("Bananas");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Empty(plan);
        Assert.Null(after);
    }

    [Fact]
    public async Task A_seasoning_never_moves_a_count()
    {
        // §13.3 is explicit that only MAIN ingredients decrement — a recipe calling for a pinch of a
        // counted spice must not cost a whole jar.
        var productId = await Product("Smoked Paprika", counted: true, onHand: 2m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Something Else Entirely", seasoningMatch: "Smoked Paprika");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Empty(plan);
        Assert.Equal(2m, after);
    }

    /// <summary>A counted product with a curated "also works as" list, and no recipe pointing at it.</summary>
    private async Task<int> CountedWithSubstitutes(string name, decimal onHand, params string[] alsoWorksAs)
    {
        var id = await Product(name, counted: true, onHand: onHand, unit: null, 1m, 1m);
        await using var db = _db.CreateDbContext();
        var p = await db.Products.SingleAsync(x => x.Id == id);
        foreach (var v in alsoWorksAs) p.Substitutes.Add(new ProductSubstitute { Value = v });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task A_main_with_no_grounded_link_is_still_decremented_when_one_counted_item_covers_it()
    {
        // The bug: makeability marks a row ✓ via IngredientMatcher (same specific food, or a curated
        // stand-in) while the decrement matched on MatchedProduct alone — so the row said "you have this"
        // and the tap beneath it moved NOTHING. Two rules for "which product does this ingredient mean".
        // This is also the only way a count on a product the recipe was saved BEFORE can be maintained,
        // since nothing back-fills MatchedProduct when a product appears (§13.8's census stock).
        var productId = await Product("Chicken Breast Tenderloins", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var recipeId = await Recipe(matchedProduct: null!); // no grounded link at all
        await using (var db = _db.CreateDbContext())
        {
            var ing = await db.RecipeIngredients.SingleAsync(i => i.RecipeId == recipeId && i.IsMain);
            ing.Name = "chicken breast";
            ing.MatchedProduct = null;
            await db.SaveChangesAsync();
        }

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal("Chicken Breast Tenderloins", Assert.Single(plan).ProductName);
        Assert.Equal(3m, after);
    }

    [Fact]
    public async Task A_curated_stand_in_is_decremented_the_same_way_the_row_goes_green()
    {
        // The other half of the matcher: the product's own "also works as" list is what lets a specific
        // recipe stay specific and still count what you own. The decrement has to honour it too.
        var productId = await CountedWithSubstitutes("Ground Turkey", 5m, "ground beef");
        var recipeId = await Recipe(matchedProduct: null!);
        await using (var db = _db.CreateDbContext())
        {
            var ing = await db.RecipeIngredients.SingleAsync(i => i.RecipeId == recipeId && i.IsMain);
            ing.Name = "ground beef";
            ing.MatchedProduct = null;
            await db.SaveChangesAsync();
        }

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal("Ground Turkey", Assert.Single(plan).ProductName);
        Assert.Equal(4m, after);
    }

    [Fact]
    public async Task Two_counted_items_covering_one_main_are_reported_rather_than_guessed_at()
    {
        // The cost of using the looser matcher: an ingredient can be covered by more than one counted
        // product. Taking a package off each would be wrong and picking one silently would be arbitrary,
        // so it decrements neither and SAYS so — a decrement declined is as much the human's business as
        // one performed.
        var beefId = await Product("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var leanId = await Product("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        var recipeId = await Recipe(matchedProduct: null!);
        await using (var db = _db.CreateDbContext())
        {
            var ing = await db.RecipeIngredients.SingleAsync(i => i.RecipeId == recipeId && i.IsMain);
            ing.Name = "ground beef";
            ing.MatchedProduct = null;
            await db.SaveChangesAsync();
        }

        await using var read = _db.CreateDbContext();
        var recipe = await read.Recipes.Include(r => r.Ingredients).SingleAsync(r => r.Id == recipeId);
        var plan = await MealStock.PlanAsync(read, recipe);

        Assert.Empty(plan.Takes);
        Assert.True(plan.NeedsConfirmation); // still worth the human's eyes
        var pick = Assert.Single(plan.Ambiguous);
        Assert.Equal("ground beef", pick.Ingredient);
        Assert.Equal(["Ground Beef Chuck", "Ground Beef Sirloin"], pick.Candidates);

        // …and nothing moved.
        await MealStock.ApplyAsync(read, recipe);
        await read.SaveChangesAsync();
        await using var after = _db.CreateDbContext();
        Assert.Equal(4m, (await after.Products.AsNoTracking().SingleAsync(p => p.Id == beefId)).QuantityOnHand);
        Assert.Equal(6m, (await after.Products.AsNoTracking().SingleAsync(p => p.Id == leanId)).QuantityOnHand);
    }

    [Fact]
    public async Task The_grounded_link_wins_outright_over_an_ambiguous_inference()
    {
        // MatchedProduct exists precisely to settle this: a human confirmed the pairing, so it beats the
        // matcher's guess and the ambiguity never arises.
        var beefId = await Product("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        await Product("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        var recipeId = await Recipe("Ground Beef Chuck");
        await using (var db = _db.CreateDbContext())
        {
            var ing = await db.RecipeIngredients.SingleAsync(i => i.RecipeId == recipeId && i.IsMain);
            ing.Name = "ground beef";
            await db.SaveChangesAsync();
        }

        var (plan, after) = await Cook(recipeId, beefId);

        Assert.Equal("Ground Beef Chuck", Assert.Single(plan).ProductName);
        Assert.Equal(3m, after);
    }

    [Fact]
    public async Task A_matched_product_whose_casing_drifted_is_still_found()
    {
        // MatchedProduct is captured at save time; a later rename to different casing must not silently
        // stop the decrement. SQLite's `IN` is case-SENSITIVE, so matching in SQL failed here with no
        // error at all — the count simply never moved.
        var productId = await Product("Beef Chuck Roast", counted: true, onHand: 3m, unit: null, 1m, 1m);
        var recipeId = await Recipe("BEEF CHUCK ROAST");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Single(plan);
        Assert.Equal(2m, after);
    }
}
