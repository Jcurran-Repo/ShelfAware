using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Core.Recipes;

/// <summary>
/// Which products count as "on hand" for recipe reasoning: tracked, in an EDIBLE aisle, and not one the
/// engine thinks you've run out of. One definition shared by the Recipes page and the recipe adapter so
/// the two can't drift.
/// </summary>
public static class PantryOnHand
{
    public static IEnumerable<Product> EdibleInStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            ReplenishmentPredictor.Predict(p, today, honorExpirations).Status != PredictionStatus.Overdue);

    /// <summary>The other side of the same rule: tracked, edible products the engine thinks you've RUN OUT
    /// of. These are exactly the items <see cref="EdibleInStock"/> silently drops — surfaced so a recipe
    /// row can say "you may still have this, it's just due for a re-buy" instead of a bare red mark.
    /// With <paramref name="honorExpirations"/> that includes EXPIRED items (an expired chicken must not
    /// count as on-hand chicken) — the two methods stay exact complements by construction.</summary>
    public static IEnumerable<Product> EdibleOutOfStock(IEnumerable<Product> products, DateOnly today, bool honorExpirations = false) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            ReplenishmentPredictor.Predict(p, today, honorExpirations).Status == PredictionStatus.Overdue);

    /// <summary>The third way a covering product can be invisible: edible but UNTRACKED. Untracked items
    /// are excluded from on-hand and run-out alike (the engine isn't allowed to watch them), which
    /// otherwise leaves a red recipe row with no explanation at all — surfaced so the row can say
    /// "you have this, but it's untracked" with a one-tap re-track.</summary>
    public static IEnumerable<Product> EdibleUntracked(IEnumerable<Product> products) =>
        products.Where(p => !p.IsTracked && p.Category.IsEdible());
}
