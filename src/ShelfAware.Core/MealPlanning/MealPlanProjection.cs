using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.MealPlanning;

/// <summary>One main ingredient of a planned meal — the name the recipe wrote, plus the product it was
/// grounded to at save time (or null). The two things <see cref="IngredientMatcher"/> needs to decide "do
/// I have this?" — no EF entity, so the projection stays pure and testable.</summary>
public sealed record PlannedIngredient(string Name, string? MatchedProduct);

/// <summary>A planned meal reduced to what the projection needs: when it's eaten, which slot, its recipe
/// name (for the "for …" label), and its MAIN ingredients (the ones that decide whether you can make it —
/// seasonings are assumed on hand, exactly as the recipe pages treat them).</summary>
public sealed record PlannedMealView(DateOnly Date, MealSlot Slot, string RecipeName, IReadOnlyList<PlannedIngredient> MainIngredients);

/// <summary>An ingredient a planned meal needs that you don't have — a plan-sourced shopping item. Derived,
/// never stored (§6): it exists because a meal in the horizon needs it and you don't have it, and it
/// vanishes the moment the meal is cooked/rerolled/removed or you buy the item. Its due date is the shopping
/// trip BEFORE the meal (meal date − a small lead, floored at today).</summary>
public sealed record PlanShopItem
{
    public required string Name { get; init; }
    /// <summary>Shop by this date — the earliest meal that needs it, minus a lead, never in the past.</summary>
    public required DateOnly DueDate { get; init; }
    /// <summary>Days until <see cref="DueDate"/>; ≤ 0 means shop now/overdue.</summary>
    public required int DaysUntil { get; init; }
    /// <summary>Bucket for the grocery list: false = "Buy now", true = "Coming up" (more than
    /// <see cref="MealPlanProjection.ComingUpDays"/> out) — a FIXED window, deliberately unlike the
    /// predictor's cadence-aware one (§5b): a plan item has a declared due date, no rhythm.</summary>
    public required bool ComingUp { get; init; }
    /// <summary>The (earliest) meal that needs it — for the "for Beef Tacos, Thu" hint.</summary>
    public required DateOnly MealDate { get; init; }
    public required MealSlot MealSlot { get; init; }
    public required string RecipeName { get; init; }
}

/// <summary>
/// The pantry projection (§6): what the plan needs from the store, computed at read time from
/// (planned meals in the horizon) + (what's on hand). Pure — writes nothing, so it can neither contradict a
/// real signal (reality is an input, recomputed every read) nor teach the predictor (principles 1–3 are one
/// idea). "Do I have this?" is <see cref="IngredientMatcher.IsSatisfied"/> and the on-hand set is
/// <see cref="PantryOnHand.EdibleInStock"/>, exactly as the recipe pages decide coverage, so the plan and
/// the recipes can't disagree.
/// </summary>
public static class MealPlanProjection
{
    /// <summary>Shop this many days BEFORE a meal — the trip before you cook it.</summary>
    public const int LeadDays = 2;

    /// <summary>How far ahead to surface plan shop items. A 30-day plan doesn't dump a month of groceries at
    /// once; items appear as their meals enter this window (derived, recomputed each read).</summary>
    public const int HorizonDays = 14;

    /// <summary>More than this many days until due → "Coming up" rather than "Buy now" (matches the
    /// dashboard's ComingUpHorizonDays).</summary>
    public const int ComingUpDays = 7;

    /// <summary>The ingredients the plan needs that you don't have, one per food, earliest-meal wins. Meals
    /// outside <paramref name="horizonDays"/> (or in the past) are ignored; a main ingredient already
    /// covered by on-hand stock is not a shop item (it's spoken for, §6). Deduped by FOOD, not by string —
    /// two meals needing "chicken breast" and "boneless chicken breast" are one item — via the same
    /// <see cref="IngredientMatcher.IsSameFood"/> the rest of the app uses.</summary>
    public static IReadOnlyList<PlanShopItem> ShopItems(
        IReadOnlyList<PlannedMealView> meals,
        IReadOnlyCollection<PantryProduct> onHand,
        DateOnly today,
        int horizonDays = HorizonDays,
        int leadDays = LeadDays)
    {
        var horizonEnd = today.AddDays(horizonDays);
        var kept = new List<PlanShopItem>();

        // Date-sorted so the FIRST time a food is seen is its earliest meal — the one that sets the due date.
        foreach (var meal in meals.Where(m => m.Date >= today && m.Date <= horizonEnd).OrderBy(m => m.Date))
        {
            foreach (var ingredient in meal.MainIngredients)
            {
                if (string.IsNullOrWhiteSpace(ingredient.Name)) continue;
                if (IngredientMatcher.IsSatisfied(ingredient.Name, ingredient.MatchedProduct, onHand)) continue;
                if (kept.Any(k => IngredientMatcher.IsSameFood(k.Name, ingredient.Name))) continue; // earliest already kept

                var due = Later(today, meal.Date.AddDays(-leadDays));
                var daysUntil = due.DayNumber - today.DayNumber;
                kept.Add(new PlanShopItem
                {
                    Name = ingredient.Name,
                    DueDate = due,
                    DaysUntil = daysUntil,
                    ComingUp = daysUntil > ComingUpDays,
                    MealDate = meal.Date,
                    MealSlot = meal.Slot,
                    RecipeName = meal.RecipeName,
                });
            }
        }

        return [.. kept.OrderBy(i => i.DueDate).ThenBy(i => i.Name)];
    }

    // Stryker disable once equality : `a >= b` and `a > b` return the same value here — when a == b both
    // yield that equal DateOnly, so no input distinguishes them.
    private static DateOnly Later(DateOnly a, DateOnly b) => a > b ? a : b;
}
