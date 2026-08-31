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
        await SaveSettings(service, new MealPlanSettings { Days = 2 });

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
        await SaveSettings(service, new MealPlanSettings { Days = 2 });
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
        await SaveSettings(service, new MealPlanSettings { Days = 1 });

        await service.GenerateAsync();

        var batch = Assert.Single(gen.Calls);
        Assert.Contains("Chicken Breast", batch.OnHand);        // on hand for cooking
        Assert.Contains("Chicken Breast", batch.CommonlyBought); // and in the familiar palette (bought before)
    }

    [Fact]
    public async Task Generate_replaces_the_plan_calendar_but_keeps_old_recipes_as_a_library()
    {
        var settings = new FakeAppSettings(); // one store, shared by both generate calls
        var service = Service(new FakeMealPlanGenerator([Meal("Old A"), Meal("Old B")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2 });
        await service.GenerateAsync();

        // Regenerate with fresh meals.
        await Service(new FakeMealPlanGenerator([Meal("New A"), Meal("New B")]), settings).GenerateAsync();

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.MealPlans.ToListAsync());                            // one active plan
        var names = await db.Recipes.Select(r => r.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["New A", "New B", "Old A", "Old B"], names);                  // old recipes KEPT as the library
        Assert.Equal(2, await db.PlannedMeals.CountAsync());                        // only the new plan is scheduled
    }

    [Fact]
    public async Task Regenerating_keeps_every_old_recipe_including_uncooked_ones()
    {
        var settings = new FakeAppSettings();
        var service = Service(new FakeMealPlanGenerator([Meal("Keeper"), Meal("Throwaway")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2 });
        await service.GenerateAsync();

        // Cook "Keeper" — a MealEvent references its recipe; that history must survive a regenerate.
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
            Assert.NotNull(await db.Recipes.FirstOrDefaultAsync(r => r.Id == keeperId));      // cooked → survives
            Assert.NotNull(await db.Recipes.FirstOrDefaultAsync(r => r.Name == "Throwaway")); // uncooked → kept too (library)
            Assert.Single(await db.MealEvents.ToListAsync());                                  // its meal-log is intact
        }
    }

    [Fact]
    public async Task Regenerating_the_same_dish_reuses_the_recipe_instead_of_making_a_twin()
    {
        var settings = new FakeAppSettings();
        var service = Service(new FakeMealPlanGenerator([Meal("Skillet Tacos")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 1 });
        await service.GenerateAsync();

        int firstId;
        await using (var db = _db.CreateDbContext())
            firstId = (await db.Recipes.SingleAsync(r => r.Name == "Skillet Tacos")).Id;

        // Regenerate the identical dish (same name + main ingredient).
        await Service(new FakeMealPlanGenerator([Meal("Skillet Tacos")]), settings).GenerateAsync();

        await using (var db = _db.CreateDbContext())
        {
            Assert.Single(await db.Recipes.Where(r => r.Name == "Skillet Tacos").ToListAsync()); // ONE, not two
            Assert.Equal(firstId, (await db.PlannedMeals.SingleAsync()).RecipeId);               // new plan reuses it
        }
    }

    [Fact]
    public async Task Two_identical_meals_in_one_plan_share_one_recipe()
    {
        var service = Service(new FakeMealPlanGenerator([Meal("Chili"), Meal("Chili")]));
        await SaveSettings(service, new MealPlanSettings { Days = 2 });

        await service.GenerateAsync();

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Recipes.Where(r => r.Name == "Chili").ToListAsync()); // one recipe…
        Assert.Equal(2, await db.PlannedMeals.CountAsync());                          // …both slots point at it
    }

    [Fact]
    public async Task Reroll_swaps_one_meal_keeps_the_others_and_keeps_the_old_recipe()
    {
        var settings = new FakeAppSettings();
        var service = Service(new FakeMealPlanGenerator([Meal("Old A"), Meal("Old B")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2 });
        await service.GenerateAsync();

        int mealId;
        await using (var db = _db.CreateDbContext())
            mealId = (await db.PlannedMeals.OrderBy(m => m.Date).FirstAsync()).Id; // the day-0 meal ("Old A")

        var result = await Service(new FakeMealPlanGenerator([Meal("New A")]), settings).RerollAsync(mealId);

        Assert.True(result.Succeeded);
        Assert.Equal("New A", result.RecipeName);
        await using (var db = _db.CreateDbContext())
        {
            var meals = await db.PlannedMeals.Include(m => m.Recipe).OrderBy(m => m.Date).ToListAsync();
            Assert.Equal("New A", meals[0].Recipe!.Name); // day 0 rerolled
            Assert.Equal("Old B", meals[1].Recipe!.Name); // day 1 untouched
            var names = await db.Recipes.Select(r => r.Name).OrderBy(n => n).ToListAsync();
            Assert.Equal(["New A", "Old A", "Old B"], names); // the swapped-out "Old A" stays in the library
        }
    }

    [Fact]
    public async Task Reroll_tells_the_generator_the_other_plan_meals_to_avoid()
    {
        var settings = new FakeAppSettings();
        var service = Service(new FakeMealPlanGenerator([Meal("Old A"), Meal("Old B")]), settings);
        await SaveSettings(service, new MealPlanSettings { Days = 2 });
        await service.GenerateAsync();
        int mealId;
        await using (var db = _db.CreateDbContext())
            mealId = (await db.PlannedMeals.OrderBy(m => m.Date).FirstAsync()).Id;

        var gen = new FakeMealPlanGenerator([Meal("New A")]);
        await Service(gen, settings).RerollAsync(mealId);

        var batch = Assert.Single(gen.Calls);
        Assert.Single(batch.Slots);                 // just the one slot
        Assert.Contains("Old A", batch.AvoidNames); // the reroll avoids the whole plan, so it differs
        Assert.Contains("Old B", batch.AvoidNames);
    }

    [Fact]
    public async Task Rerolling_a_missing_meal_is_a_soft_failure()
    {
        var result = await Service(new FakeMealPlanGenerator([Meal("X")])).RerollAsync(99999);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Plan_days_reflects_the_covered_span_not_the_raw_request()
    {
        // Days = 0 is below the clamp floor; one day still generates. The stored Days must be the covered
        // span (1), never the raw 0 — otherwise MealCalendar renders an empty grid.
        var service = Service(new FakeMealPlanGenerator([Meal("Solo")]));
        await SaveSettings(service, new MealPlanSettings { Days = 0 });

        await service.GenerateAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, (await db.MealPlans.SingleAsync()).Days);
    }

    [Fact]
    public async Task Generate_batches_a_long_horizon_and_carries_the_already_planned_names_forward()
    {
        // 10 dinners > BatchSize (7) → two calls: 7 then 3. The second must be told the first batch's names.
        var first = Enumerable.Range(1, 7).Select(i => Meal($"Meal {i}")).ToArray();
        var second = Enumerable.Range(8, 3).Select(i => Meal($"Meal {i}")).ToArray();
        var gen = new FakeMealPlanGenerator(first, second);
        var service = Service(gen);
        await SaveSettings(service, new MealPlanSettings { Days = 10 });

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
        await SaveSettings(service, new MealPlanSettings { Days = 1 });

        var result = await service.GenerateAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.MealPlans.ToListAsync());
        Assert.Empty(await db.Recipes.ToListAsync());
    }

    [Fact]
    public async Task Each_meal_row_expands_to_a_slot_carrying_its_resolved_calories_and_effort()
    {
        // The heart of per-meal settings: a snack row overrides to 150 cal / quick while the dinner row
        // inherits the plan defaults — and each reaches the generator as its own slot.
        var gen = new FakeMealPlanGenerator([Meal("Dinner dish"), Meal("Snack dish")]);
        var service = Service(gen);
        await SaveSettings(service, new MealPlanSettings
        {
            Days = 1,
            DefaultCalories = 500,
            DefaultEffort = TimeEffort.Standard,
            Meals =
            [
                new MealEntry { Slot = MealSlot.Dinner },                                          // inherits
                new MealEntry { Slot = MealSlot.Snack, Calories = 150, Effort = TimeEffort.Quick }, // overrides
            ],
        });

        await service.GenerateAsync();

        var slots = Assert.Single(gen.Calls).Slots;
        Assert.Equal(2, slots.Count);                                     // 1 day × 2 meals
        var dinner = slots.Single(s => s.Slot == MealSlot.Dinner);
        Assert.Equal(500, dinner.Calories);                              // inherited default
        Assert.Equal(TimeEffort.Standard, dinner.Effort);
        var snack = slots.Single(s => s.Slot == MealSlot.Snack);
        Assert.Equal(150, snack.Calories);                              // per-meal override
        Assert.Equal(TimeEffort.Quick, snack.Effort);
    }

    [Fact]
    public async Task Settings_round_trip_through_the_store()
    {
        var service = Service(new FakeMealPlanGenerator());
        var saved = new MealPlanSettings
        {
            Days = 14,
            Meals = [new MealEntry { Slot = MealSlot.Breakfast }, new MealEntry { Slot = MealSlot.Snack, Calories = 150, Effort = TimeEffort.Quick }],
            DefaultCalories = 550, DefaultEffort = TimeEffort.Quick, ProteinGramsPerDay = 120,
            FoodGroups = ["vegetables", "lean protein"], Appliances = ["slow cooker"], Invent = true, PreferLeftovers = true,
        };

        await service.SaveSettingsAsync(saved);
        var loaded = await service.LoadSettingsAsync();

        Assert.Equal(14, loaded.Days);
        Assert.Equal(2, loaded.Meals.Count);
        Assert.Equal(MealSlot.Breakfast, loaded.Meals[0].Slot);
        Assert.Equal(MealSlot.Snack, loaded.Meals[1].Slot);
        Assert.Equal(150, loaded.Meals[1].Calories);                    // per-meal override survives
        Assert.Equal(TimeEffort.Quick, loaded.Meals[1].Effort);
        Assert.Equal(550, loaded.DefaultCalories);
        Assert.Equal(120, loaded.ProteinGramsPerDay);
        Assert.Equal(TimeEffort.Quick, loaded.DefaultEffort);
        Assert.Equal(["vegetables", "lean protein"], loaded.FoodGroups);
        Assert.Equal(["slow cooker"], loaded.Appliances);
        Assert.True(loaded.Invent);
        Assert.True(loaded.PreferLeftovers);
    }

    [Fact]
    public async Task Load_settings_defaults_when_nothing_is_saved()
    {
        var loaded = await Service(new FakeMealPlanGenerator()).LoadSettingsAsync();

        Assert.Equal(7, loaded.Days);
        Assert.Equal([MealSlot.Dinner], loaded.Meals.Select(m => m.Slot));
        Assert.False(loaded.Invent);
        Assert.False(loaded.PreferLeftovers);
        Assert.Null(loaded.DefaultCalories);
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
