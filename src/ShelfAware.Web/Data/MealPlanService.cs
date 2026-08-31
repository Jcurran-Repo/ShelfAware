using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;

namespace ShelfAware.Web.Data;

/// <summary>
/// Orchestrates meal planning: loads the household's setup + pantry, generates the plan a week at a time
/// (<see cref="IMealPlanGenerator"/>), and assembles it. ONE active plan per household, so generating
/// REPLACES the old plan's CALENDAR — but every generated recipe is KEPT as a reusable library (idea #1:
/// mix-and-match across months). Regenerating a dish the library already holds REUSES that recipe rather
/// than creating a twin (<see cref="RecipeSignature"/> dedup). Also owns the setup's AppSettings
/// persistence and reading the current plan back for display.
/// <para>Generation happens BEFORE any replace, so a failed AI call leaves the existing plan untouched.</para>
/// </summary>
public sealed class MealPlanService(
    IHouseholdDbFactory dbFactory,
    IMealPlanGenerator generator,
    IAppSettings settings,
    ILogger<MealPlanService> logger)
{
    // A generation call is capped near this many slots so the model returns full recipes within its output
    // budget; a longer horizon is generated over several calls, each told the names already planned so the
    // whole plan stays varied.
    private const int BatchSize = 7;

    // Guards against a misconfigured horizon turning into dozens of AI calls. A month of four meals a day
    // is 124 slots; beyond that we cap and say so rather than silently spend.
    private const int MaxSlots = 124;

    public async Task<MealPlanSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        var json = await settings.GetAsync(SettingKeys.MealPlanSettings, ct);
        if (string.IsNullOrEmpty(json)) return new MealPlanSettings();
        try
        {
            return JsonSerializer.Deserialize<MealPlanSettings>(json) ?? new MealPlanSettings();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored meal-plan settings no longer parse — using defaults.");
            return new MealPlanSettings();
        }
    }

    public Task SaveSettingsAsync(MealPlanSettings mealSettings, CancellationToken ct = default) =>
        settings.SetAsync(SettingKeys.MealPlanSettings, JsonSerializer.Serialize(mealSettings), ct);

    /// <summary>The household's active plan (newest), with its meals and their recipes, or null if none.</summary>
    public async Task<MealPlan?> GetCurrentPlanAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MealPlans.AsNoTracking()
            .Include(p => p.Meals).ThenInclude(m => m.Recipe).ThenInclude(r => r!.Ingredients)
            .Include(p => p.Meals).ThenInclude(m => m.Recipe).ThenInclude(r => r!.Steps)
            // ⚠️ Order by Id, not CreatedAt: SQLite refuses a DateTimeOffset in ORDER BY (repo gotcha).
            // Insert order is chronological, and there's one active plan anyway (regenerate replaces).
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Generate a fresh plan from the current setup + pantry, replacing any existing plan. A bad
    /// batch is retried by the generator and otherwise tolerated (a shorter plan, not a crash); a systematic
    /// failure returns a soft <see cref="MealPlanResult"/> error with any existing plan left intact (nothing
    /// is deleted until generation has produced meals). <paramref name="onProgress"/> reports (batches done,
    /// total) for the background job's status.</summary>
    public async Task<MealPlanResult> GenerateAsync(Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var setup = await LoadSettingsAsync(ct);
        var slots = SlotsFor(setup); // always ≥ 1 — Days clamps to [1,31] and Slots defaults to dinner
        var chunks = slots.Chunk(BatchSize).ToList();
        var context = await LoadContextAsync(setup, ct);
        onProgress?.Invoke(0, chunks.Count);

        // Generate a batch at a time, telling each batch what's already planned so the plan stays varied.
        var planned = new List<(PlannedSlot Slot, RecipeSuggestion Meal)>();
        var alreadyPlanned = new List<string>();
        for (var b = 0; b < chunks.Count; b++)
        {
            var chunk = chunks[b];
            var batch = new MealPlanBatch(
                chunk, setup, context.OnHand, context.CommonlyBought, context.Expiring,
                context.Excluded, context.SavedRecipes, [.. alreadyPlanned]); // snapshot — the list keeps growing
            var meals = await generator.GenerateAsync(batch, ct); // never throws except on cancellation
            for (var i = 0; i < chunk.Length && i < meals.Count; i++)
            {
                planned.Add((chunk[i], meals[i]));
                alreadyPlanned.Add(meals[i].Name);
            }
            if (meals.Count < chunk.Length)
                logger.LogWarning("A meal-plan batch returned {Got} of {Asked} meals.", meals.Count, chunk.Length);

            // Fast-fail a systematic problem (bad key, model unavailable): if the FIRST batch produced
            // nothing, don't burn the rest of the calls — bail, leaving any existing plan intact.
            if (b == 0 && planned.Count == 0)
                return MealPlanResult.Failed("Couldn't generate any meals just now — please try again.");
            onProgress?.Invoke(b + 1, chunks.Count);
        }

        if (planned.Count == 0)
            return MealPlanResult.Failed("Couldn't generate any meals just now — please try again.");

        var planId = await PersistAsync(setup, planned, ct);
        logger.LogInformation("Generated a meal plan of {Count} meal(s) over {Days} day(s).", planned.Count, setup.Days);
        return MealPlanResult.Ok(planId, planned.Count);
    }

    // Replace the current plan's CALENDAR and create the new one — in one transaction, AFTER generation
    // succeeded. Every generated recipe is KEPT as a reusable library (idea #1), so a dish the library
    // already holds is REUSED (its PlannedMeal points at the existing recipe) rather than creating a twin —
    // deduped by <see cref="RecipeSignature"/> (name + main ingredients), across past recipes AND within
    // this batch.
    private async Task<int> PersistAsync(
        MealPlanSettings setup, IReadOnlyList<(PlannedSlot Slot, RecipeSuggestion Meal)> planned, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await RemoveOldPlanAsync(db, ct);

        var recipeIdBySignature = await LoadLibraryBySignatureAsync(db, ct);
        // Days = the span actually generated, not the raw request — SlotsFor clamps days to [1,31] and caps
        // the total at MaxSlots, so a 31-day × many-meal plan may cover fewer days; the calendar must render
        // exactly what was planned, not empty weeks. (planned is non-empty here — GenerateAsync guarantees it.)
        var coveredDays = planned.Max(p => p.Slot.Day) + 1;
        var plan = new MealPlan { CreatedAt = DateTimeOffset.Now, StartDate = today, Days = coveredDays };
        var newBySignature = new Dictionary<string, Recipe>(); // dedup two same-signature meals in one plan
        foreach (var (slot, meal) in planned)
        {
            var signature = SignatureOf(meal);
            var plannedMeal = new PlannedMeal { Date = today.AddDays(slot.Day), Slot = slot.Slot };
            if (recipeIdBySignature.TryGetValue(signature, out var existingId))
            {
                plannedMeal.RecipeId = existingId;                 // reuse a recipe already on file
            }
            else if (newBySignature.TryGetValue(signature, out var justAdded))
            {
                plannedMeal.Recipe = justAdded;                   // reuse one created earlier in this batch
            }
            else
            {
                var recipe = BuildRecipe(meal);
                newBySignature[signature] = recipe;
                plannedMeal.Recipe = recipe;
            }
            plan.Meals.Add(plannedMeal);
        }
        db.MealPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return plan.Id;
    }

    /// <summary>Regenerate ONE meal in the current plan, keeping the rest — the calendar's per-slot reroll.
    /// The single-slot generation is told every recipe already in the plan (so the swap is genuinely
    /// different), the new dish reuses a library recipe when one matches (same <see cref="RecipeSignature"/>
    /// dedup as generation) else creates one, and the meal is repointed in a transaction. The old recipe
    /// stays in the library (nothing is deleted). One AI call, so it runs inline on the circuit.</summary>
    public async Task<RerollResult> RerollAsync(int plannedMealId, CancellationToken ct = default)
    {
        var setup = await LoadSettingsAsync(ct);
        var context = await LoadContextAsync(setup, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var meal = await db.PlannedMeals.Include(m => m.MealPlan)
            .FirstOrDefaultAsync(m => m.Id == plannedMealId, ct);
        if (meal?.MealPlan is null) return RerollResult.Failed("That meal is no longer in your plan.");

        // This slot's targets: its meal-row override if the setup still has one for the slot, else defaults.
        var entry = setup.Meals.FirstOrDefault(m => m.Slot == meal.Slot);
        var calories = entry is not null ? setup.CaloriesFor(entry) : setup.DefaultCalories;
        var effort = entry is not null ? setup.EffortFor(entry) : setup.DefaultEffort;
        var dayOffset = meal.Date.DayNumber - meal.MealPlan.StartDate.DayNumber;
        var slot = new PlannedSlot(dayOffset, meal.Slot, calories, effort);

        // Avoid every recipe already in the plan (including the one being rerolled) so the swap differs.
        var planNames = await db.PlannedMeals.Where(m => m.MealPlanId == meal.MealPlanId)
            .Select(m => m.Recipe!.Name).ToListAsync(ct);
        var batch = new MealPlanBatch([slot], setup, context.OnHand, context.CommonlyBought,
            context.Expiring, context.Excluded, context.SavedRecipes, planNames);

        var meals = await generator.GenerateAsync(batch, ct); // never throws except on cancellation
        if (meals.Count == 0)
            return RerollResult.Failed("Couldn't come up with a different meal just now — please try again.");
        var suggestion = meals[0];

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Library dedup is best-effort: two rerolls racing from two tabs can't see each other's uncommitted
        // recipe, so they could each create a twin of the same new dish. Harmless (a duplicate library row,
        // no data loss) and rare (two concurrent rerolls of the same generated dish); not worth a lock.
        var recipeIdBySignature = await LoadLibraryBySignatureAsync(db, ct);
        if (recipeIdBySignature.TryGetValue(SignatureOf(suggestion), out var existingId))
            meal.RecipeId = existingId;             // reuse a library recipe (incl. the swapped-out one if identical)
        else
            meal.Recipe = BuildRecipe(suggestion);  // a fresh recipe joins the library
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RerollResult.Ok(suggestion.Name);
    }

    // The library by signature (earliest id wins) — every recipe on file, plan-generated or user-kept — so a
    // dish already saved is reused rather than duplicated. Shared by generation and reroll.
    private static async Task<Dictionary<string, int>> LoadLibraryBySignatureAsync(ShelfAwareDbContext db, CancellationToken ct)
    {
        var existing = await db.Recipes
            .Select(r => new { r.Id, r.Name, Mains = r.Ingredients.Where(i => i.IsMain).Select(i => i.Name) })
            .ToListAsync(ct);
        var map = new Dictionary<string, int>();
        foreach (var r in existing) map.TryAdd(RecipeSignature.Of(r.Name, r.Mains), r.Id);
        return map;
    }

    private static string SignatureOf(RecipeSuggestion meal) =>
        RecipeSignature.Of(meal.Name, meal.Ingredients.Where(i => i.IsMain).Select(i => i.Name));

    // Build a plan-generated Recipe entity from a suggestion. Shared by generation and reroll so a planned
    // meal reads back identically however it was produced.
    private static Recipe BuildRecipe(RecipeSuggestion meal) => new()
    {
        Name = meal.Name,
        Blurb = meal.Blurb,
        SavedAt = DateTimeOffset.Now,
        PlanGenerated = true,
        EstimatedCaloriesPerServing = meal.CaloriesPerServing,
        Ingredients = meal.Ingredients.Select(i => new RecipeIngredient
        {
            Name = i.Name, IsMain = i.IsMain, MatchedProduct = i.MatchedProduct, Quantity = i.Quantity,
        }).ToList(),
        Steps = meal.Steps.Select((t, idx) => new RecipeStep { Order = idx + 1, Text = t }).ToList(),
    };

    // Replace the current plan's CALENDAR but KEEP every generated recipe as a reusable library (idea #1:
    // mix-and-match across months). Removing the MealPlan cascades its PlannedMeals; the recipes those
    // pointed to survive (a recipe is the OTHER parent of a PlannedMeal, not deleted with the plan),
    // orphaned until a future plan reuses one (PersistAsync dedups) or the user browses them under the
    // Cookbook's "Meal-plan recipes" filter. Nothing is deleted here, so cooked history is safe too.
    private static async Task RemoveOldPlanAsync(ShelfAwareDbContext db, CancellationToken ct)
    {
        var oldPlans = await db.MealPlans.ToListAsync(ct);
        if (oldPlans.Count == 0) return;
        db.MealPlans.RemoveRange(oldPlans); // cascades the PlannedMeals; recipes stay as the library
        await db.SaveChangesAsync(ct);
    }

    // Every meal to fill, day-major (day 0's meals, then day 1's…) so a BatchSize chunk spans roughly a week.
    // Each meal resolves its own calorie + effort target (its override, or the plan default) so the generator
    // prompts a snack as a snack and a dinner as a dinner. Days is clamped and the total is capped (MaxSlots).
    private static IReadOnlyList<PlannedSlot> SlotsFor(MealPlanSettings setup)
    {
        var days = Math.Clamp(setup.Days, 1, 31);
        var meals = setup.Meals.Count > 0 ? setup.Meals : [new MealEntry { Slot = MealSlot.Dinner }];
        var slots = new List<PlannedSlot>();
        for (var day = 0; day < days; day++)
        {
            foreach (var meal in meals)
            {
                slots.Add(new PlannedSlot(day, meal.Slot, setup.CaloriesFor(meal), setup.EffortFor(meal)));
                if (slots.Count >= MaxSlots) return slots;
            }
        }
        return slots;
    }

    private async Task<PantryContext> LoadContextAsync(MealPlanSettings setup, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var trackExpirations = await settings.GetTrackExpirationDatesAsync(ct);

        var products = await db.Products.AsNoTracking()
            .Include(p => p.Purchases).Include(p => p.Signals).ToListAsync(ct);

        var onHand = PantryOnHand.EdibleInStock(products, today, trackExpirations)
            .Select(p => p.Name).OrderBy(n => n).ToList();

        // The "familiar palette": edible products they've actually bought, most-bought first (a light cap so
        // the prompt stays focused on what they really cook with).
        var commonlyBought = products
            .Where(p => p.Category.IsEdible() && p.Purchases.Count > 0)
            .OrderByDescending(p => p.Purchases.Count)
            .Select(p => p.Name)
            .Take(40).ToList();

        // Expiring-first hint (only when the household tracks dates): on-hand items whose latest purchase's
        // date lands within a week. A soft nudge for the prompt, not the precise engine cap (§7).
        var expiring = !trackExpirations ? [] : PantryOnHand.EdibleInStock(products, today, trackExpirations)
            .Where(p => p.Purchases.Any(pe => pe.ExpirationDate is { } d && d >= today && d <= today.AddDays(7)))
            .Select(p => p.Name).OrderBy(n => n).ToList();

        var excluded = await db.ExcludedFoods.AsNoTracking().Select(f => f.Value).ToListAsync(ct);
        // The household's own saved recipes (not the planner's own output) — inspiration for adapt-known.
        var saved = await db.Recipes.AsNoTracking().Where(r => !r.PlanGenerated).Select(r => r.Name).ToListAsync(ct);

        return new PantryContext(onHand, commonlyBought, expiring, excluded, saved);
    }

    private sealed record PantryContext(
        IReadOnlyList<string> OnHand,
        IReadOnlyList<string> CommonlyBought,
        IReadOnlyList<string> Expiring,
        IReadOnlyList<string> Excluded,
        IReadOnlyList<string> SavedRecipes);
}

/// <summary>The outcome of a generate: the new plan's id + meal count, or a human-readable error (a soft
/// failure like "nothing to plan" — a hard AI error propagates as an exception for the caller to catch).</summary>
public sealed record MealPlanResult(int PlanId, int MealCount, string? Error)
{
    public bool Succeeded => Error is null;
    public static MealPlanResult Ok(int planId, int mealCount) => new(planId, mealCount, null);
    public static MealPlanResult Failed(string error) => new(0, 0, error);
}

/// <summary>The outcome of a per-slot reroll: the new meal's name, or a soft human-readable error.</summary>
public sealed record RerollResult(string? RecipeName, string? Error)
{
    public bool Succeeded => Error is null;
    public static RerollResult Ok(string recipeName) => new(recipeName, null);
    public static RerollResult Failed(string error) => new(null, error);
}
