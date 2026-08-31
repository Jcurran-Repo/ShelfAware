using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The meal-plan orchestration on real EF/SQLite with a FAKED generator: assembling generated meals into a
/// plan + plan-generated recipes dated by slot, replacing the old plan on regenerate (while KEEPING any
/// recipe that was cooked or kept), batching a long horizon and carrying the already-planned names forward,
/// and the settings round-trip.
/// </summary>
public class MealPlanServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private MealPlanService Service(FakeMealPlanGenerator generator, FakeAppSettings? settings = null) =>
        new(_db, generator, settings ?? new FakeAppSettings(), NullLogger<MealPlanService>.Instance);

    private static RecipeSuggestion Meal(string name, string main = "chicken") => new(
        name, $"A {name} dinner.",
        [new SuggestedIngredient(main, true, null, "1 lb"), new SuggestedIngredient("salt", false, null)],
        ["Cook it.", "Serve it."], 500);

    private static async Task SaveSettings(MealPlanService service, MealPlanSettings settings) =>
        await service.SaveSettingsAsync(settings);

    [Fact]
    public async Task Generate_assembles_a_plan_with_plan_generated_recipes_dated_by_slot()
    {
        var gen = new FakeMealPlanGenerator([Meal("Tacos"), Meal("Chili")]);
        var settings = new FakeAppSettings();
        var service = Service(gen, settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2, Slots = [MealSlot.Dinner] });

        var result = await service.GenerateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.MealCount);

        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = _db.CreateDbContext();
        var plan = await db.MealPlans.Include(p => p.Meals).ThenInclude(m => m.Recipe).ThenInclude(r => r!.Ingredients)
            .Include(p => p.Meals).ThenInclude(m => m.Recipe).ThenInclude(r => r!.Steps)
            .SingleAsync();
        Assert.Equal(today, plan.StartDate);
        Assert.Equal(2, plan.Meals.Count);
        var day0 = plan.Meals.Single(m => m.Date == today);
        Assert.Equal(MealSlot.Dinner, day0.Slot);
        Assert.Equal("Tacos", day0.Recipe!.Name);
        Assert.True(day0.Recipe.PlanGenerated);                 // hidden from the Cookbook until "kept"
        Assert.Equal(500, day0.Recipe.EstimatedCaloriesPerServing);
        Assert.Contains(day0.Recipe.Ingredients, i => i.Name == "chicken" && i.IsMain);
        Assert.NotEmpty(day0.Recipe.Steps);
        Assert.Equal("Chili", plan.Meals.Single(m => m.Date == today.AddDays(1)).Recipe!.Name);
    }

    [Fact]
    public async Task Get_current_plan_returns_the_active_plan_with_its_meals_and_recipes()
    {
        // Exercises the real GetCurrentPlanAsync query (Include chain + order) on SQLite — the OrderBy must
        // not use CreatedAt (a DateTimeOffset SQLite refuses in ORDER BY; the repo gotcha this pins).
        var service = Service(new FakeMealPlanGenerator([Meal("Tacos"), Meal("Chili")]));
        await SaveSettings(service, new MealPlanSettings { Days = 2, Slots = [MealSlot.Dinner] });
        await service.GenerateAsync();

        var current = await service.GetCurrentPlanAsync();

        Assert.NotNull(current);
        Assert.Equal(2, current.Meals.Count);
        Assert.Contains(current.Meals, m => m.Recipe!.Name == "Tacos" && m.Recipe.Steps.Count > 0);
    }

    [Fact]
    public async Task Get_current_plan_is_null_when_there_is_none()
    {
        Assert.Null(await Service(new FakeMealPlanGenerator()).GetCurrentPlanAsync());
    }

    [Fact]
    public async Task Generate_passes_the_on_hand_pantry_to_the_generator()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Chicken Breast",
                Category = Category.Meat,
                Purchases = [new PurchaseEvent { PurchasedAt = DateOnly.FromDateTime(DateTime.Today).AddDays(-2), Quantity = 1 }],
            });
            await db.SaveChangesAsync();
        }
        var gen = new FakeMealPlanGenerator([Meal("Roast")]);
        var service = Service(gen, new FakeAppSettings());
        await SaveSettings(service, new MealPlanSettings { Days = 1, Slots = [MealSlot.Dinner] });

        await service.GenerateAsync();

        var batch = Assert.Single(gen.Calls);
        Assert.Contains("Chicken Breast", batch.OnHand);        // on hand for cooking
        Assert.Contains("Chicken Breast", batch.CommonlyBought); // and in the familiar palette (bought before)
    }

    [Fact]
    public async Task Generate_replaces_the_old_plan_and_deletes_its_unkept_recipes()
    {
        var settings = new FakeAppSettings(); // one store, shared by both generate calls
        var service = Service(new FakeMealPlanGenerator([Meal("Old A"), Meal("Old B")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2, Slots = [MealSlot.Dinner] });
        await service.GenerateAsync();

        // Regenerate with fresh meals.
        await Service(new FakeMealPlanGenerator([Meal("New A"), Meal("New B")]), settings).GenerateAsync();

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.MealPlans.ToListAsync());                            // one active plan
        var names = await db.Recipes.Select(r => r.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["New A", "New B"], names);                                    // the old plan's recipes are gone
        Assert.Equal(2, await db.PlannedMeals.CountAsync());
    }

    [Fact]
    public async Task Generate_keeps_a_cooked_plan_recipe_when_it_replaces_the_plan()
    {
        var settings = new FakeAppSettings();
        var service = Service(new FakeMealPlanGenerator([Meal("Keeper"), Meal("Throwaway")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2, Slots = [MealSlot.Dinner] });
        await service.GenerateAsync();

        // Cook "Keeper" — a MealEvent now references its recipe, so regenerating must not erase that history.
        int keeperId;
        await using (var db = _db.CreateDbContext())
        {
            keeperId = (await db.Recipes.SingleAsync(r => r.Name == "Keeper")).Id;
            db.MealEvents.Add(new MealEvent { RecipeId = keeperId, AteAt = DateOnly.FromDateTime(DateTime.Today) });
            await db.SaveChangesAsync();
        }

        await Service(new FakeMealPlanGenerator([Meal("Fresh A"), Meal("Fresh B")]), settings).GenerateAsync();

        await using (var db = _db.CreateDbContext())
        {
            Assert.NotNull(await db.Recipes.FirstOrDefaultAsync(r => r.Id == keeperId)); // cooked → survives
            Assert.Null(await db.Recipes.FirstOrDefaultAsync(r => r.Name == "Throwaway")); // uncooked → deleted
            Assert.Single(await db.MealEvents.ToListAsync());                              // its meal-log is intact
        }
    }

    [Fact]
    public async Task Generate_batches_a_long_horizon_and_carries_the_already_planned_names_forward()
    {
        // 10 dinners > BatchSize (7) → two calls: 7 then 3. The second must be told the first batch's names.
        var first = Enumerable.Range(1, 7).Select(i => Meal($"Meal {i}")).ToArray();
        var second = Enumerable.Range(8, 3).Select(i => Meal($"Meal {i}")).ToArray();
        var gen = new FakeMealPlanGenerator(first, second);
        var service = Service(gen);
        await SaveSettings(service, new MealPlanSettings { Days = 10, Slots = [MealSlot.Dinner] });

        var result = await service.GenerateAsync();

        Assert.Equal(10, result.MealCount);
        Assert.Equal(2, gen.Calls.Count);
        Assert.Empty(gen.Calls[0].AvoidNames);                    // nothing planned before the first batch
        Assert.Contains("Meal 1", gen.Calls[1].AvoidNames);       // the second batch avoids the first's meals
        Assert.Contains("Meal 7", gen.Calls[1].AvoidNames);
    }

    [Fact]
    public async Task Generate_with_no_meals_from_the_model_is_a_soft_failure_and_writes_nothing()
    {
        var service = Service(new FakeMealPlanGenerator([])); // the model came back empty
        await SaveSettings(service, new MealPlanSettings { Days = 1, Slots = [MealSlot.Dinner] });

        var result = await service.GenerateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.MealPlans.ToListAsync());
        Assert.Empty(await db.Recipes.ToListAsync());
    }

    [Fact]
    public async Task Settings_round_trip_through_the_store()
    {
        var service = Service(new FakeMealPlanGenerator());
        var saved = new MealPlanSettings
        {
            Days = 14, Slots = [MealSlot.Breakfast, MealSlot.Dinner], CaloriesPerMeal = 550,
            ProteinGramsPerDay = 120, Effort = TimeEffort.Quick,
            FoodGroups = ["vegetables", "lean protein"], Appliances = ["slow cooker"], Invent = true,
        };

        await service.SaveSettingsAsync(saved);
        var loaded = await service.LoadSettingsAsync();

        Assert.Equal(14, loaded.Days);
        Assert.Equal([MealSlot.Breakfast, MealSlot.Dinner], loaded.Slots);
        Assert.Equal(550, loaded.CaloriesPerMeal);
        Assert.Equal(120, loaded.ProteinGramsPerDay);
        Assert.Equal(TimeEffort.Quick, loaded.Effort);
        Assert.Equal(["vegetables", "lean protein"], loaded.FoodGroups);
        Assert.Equal(["slow cooker"], loaded.Appliances);
        Assert.True(loaded.Invent);
    }

    [Fact]
    public async Task Load_settings_defaults_when_nothing_is_saved()
    {
        var loaded = await Service(new FakeMealPlanGenerator()).LoadSettingsAsync();

        Assert.Equal(7, loaded.Days);
        Assert.Equal([MealSlot.Dinner], loaded.Slots);
        Assert.False(loaded.Invent);
        Assert.Null(loaded.CaloriesPerMeal);
    }
}

/// <summary>A scriptable meal-plan generator: returns a queued list per call (empty once exhausted) and
/// records the batches it was asked for, so the service's context-building + batching are testable without
/// a live API.</summary>
internal sealed class FakeMealPlanGenerator : IMealPlanGenerator
{
    private readonly Queue<IReadOnlyList<RecipeSuggestion>> _results;
    public List<MealPlanBatch> Calls { get; } = [];

    public FakeMealPlanGenerator(params IReadOnlyList<RecipeSuggestion>[] batchResults) =>
        _results = new Queue<IReadOnlyList<RecipeSuggestion>>(batchResults);

    public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default)
    {
        Calls.Add(batch);
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : (IReadOnlyList<RecipeSuggestion>)[]);
    }
}
