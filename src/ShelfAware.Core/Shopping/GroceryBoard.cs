using ShelfAware.Core.Domain;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Shopping;

/// <summary>Why an item is on the shopping list — the predictor's rhythm, or a meal in the plan. THE one
/// definition of a grocery row's provenance (its tag + tint), consumed by both the grocery list and the
/// dashboard so the two can't drift (the copy-pasted-fact-in-two-places defect this repo pays most for).</summary>
public enum ShoppingSource { Predictor, Plan }

/// <summary>The label + CSS accent for a <see cref="ShoppingSource"/> — a one-word tag rides with the tint
/// because color alone can't carry meaning (a11y / colorblind / screen readers).</summary>
public static class ShoppingProvenance
{
    /// <summary>The short source tag shown on a row ("Plan"); predictor rows show their status chip instead,
    /// so they carry no source tag.</summary>
    public static string? Tag(ShoppingSource source) => source == ShoppingSource.Plan ? "Plan" : null;

    /// <summary>The CSS accent suffix for the row tint (<c>.grocery-src-plan</c> etc.).</summary>
    public static string Accent(ShoppingSource source) => source == ShoppingSource.Plan ? "plan" : "predictor";
}

/// <summary>
/// One row of the combined shopping list — a predictor item, a plan item, or a predictor item the plan ALSO
/// needs. Carries the bucket (Buy now / Coming up), the aisle + due date the list sorts by, and whichever
/// side's detail applies (<see cref="Estimate"/> for predictor richness, <see cref="PlanFor"/> for the meal
/// a plan item feeds). Built by <see cref="GroceryBoard.Combine"/>.
/// </summary>
public sealed record GroceryRow
{
    public required string Name { get; init; }
    public required ShoppingSource Source { get; init; }
    /// <summary>false = "Coming up".</summary>
    public required bool BuyNow { get; init; }
    /// <summary>The store aisle to sort within; null when a plan ingredient isn't a tracked product (no
    /// known aisle) — those sort after the known aisles.</summary>
    public Category? Category { get; init; }
    public DateOnly? DueDate { get; init; }
    /// <summary>Days until due; negative = overdue. Drives the sub-sort within an aisle and the countdown.</summary>
    public int? DaysUntil { get; init; }
    /// <summary>The predictor estimate behind a predictor row (cost, qty, size, actions), or null for a
    /// plan-only row.</summary>
    public ProductEstimate? Estimate { get; init; }
    /// <summary>The (earliest) planned meal that also wants this — set on a plan-only row AND on a predictor
    /// row the plan also needs, so a row can say "for Beef Tacos, Thu" either way.</summary>
    public PlanShopItem? PlanFor { get; init; }
}

/// <summary>
/// Combines the two shopping provenances into one list: predictor estimates (rhythm) + plan shop items
/// (§6 projection), deduped by FOOD. When both want the same food, the richer PREDICTOR row is kept and
/// annotated with the meal (adopting the earlier due date) rather than shown twice — the earliest-card
/// dedup, so you never buy chicken twice. The ONE place provenance + dedup live; the grocery list and the
/// dashboard both call this.
/// </summary>
public static class GroceryBoard
{
    /// <summary>Merge predictor estimates (those with a real status — Overdue / DueSoon / Stocked; NOT the
    /// still-learning Unknowns, which stay in their own section) with the plan's shop items. Result is
    /// unsorted; the surface sorts by aisle → due date as it already does.</summary>
    public static IReadOnlyList<GroceryRow> Combine(
        IReadOnlyList<ProductEstimate> predictor, IReadOnlyList<PlanShopItem> plan)
    {
        var rows = new List<GroceryRow>(predictor.Count + plan.Count);
        foreach (var e in predictor)
        {
            rows.Add(new GroceryRow
            {
                Name = e.Name,
                Source = ShoppingSource.Predictor,
                BuyNow = e.Status is PredictionStatus.Overdue or PredictionStatus.DueSoon,
                Category = e.Category,
                DueDate = e.NextBuyDate,
                DaysUntil = e.DaysUntil,
                Estimate = e,
            });
        }

        foreach (var item in plan)
        {
            // Earliest-card dedup: a predictor row for the same food already covers this buy. Annotate it
            // with the meal and pull its due date/bucket earlier if the plan needs it sooner, rather than
            // adding a second row for the same product.
            var twinIndex = rows.FindIndex(r =>
                r.Source == ShoppingSource.Predictor && IngredientMatcher.IsSameFood(r.Name, item.Name));
            if (twinIndex >= 0)
            {
                var twin = rows[twinIndex];
                var planSooner = twin.DueDate is not { } d || item.DueDate < d;
                rows[twinIndex] = twin with
                {
                    PlanFor = twin.PlanFor ?? item, // keep the earliest meal already noted
                    DueDate = planSooner ? item.DueDate : twin.DueDate,
                    DaysUntil = planSooner ? item.DaysUntil : twin.DaysUntil,
                    BuyNow = twin.BuyNow || !item.ComingUp,
                };
                continue;
            }

            rows.Add(new GroceryRow
            {
                Name = item.Name,
                Source = ShoppingSource.Plan,
                BuyNow = !item.ComingUp,
                Category = null, // a plan ingredient that isn't a tracked product has no known aisle
                DueDate = item.DueDate,
                DaysUntil = item.DaysUntil,
                PlanFor = item,
            });
        }

        return rows;
    }
}
