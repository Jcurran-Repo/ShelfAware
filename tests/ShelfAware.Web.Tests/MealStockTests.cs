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
        return (plan, (await read.Products.AsNoTracking().SingleAsync(p => p.Id == productId)).QuantityOnHand);
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
        // Not a round pound — a pound is not a unit of anything about how this household buys.
        var productId = await Product("Ground Beef", counted: true, onHand: 5m, unit: "lb", 1.18m, 1.24m, 1.31m);
        var recipeId = await Recipe("Ground Beef");

        var (plan, after) = await Cook(recipeId, productId);

        Assert.Equal(1.24m, Assert.Single(plan).Amount);
        Assert.Equal(3.76m, after);
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
        Assert.Equal(8m, Assert.Single(current).OnHand);
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

        Assert.Empty(current);
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
