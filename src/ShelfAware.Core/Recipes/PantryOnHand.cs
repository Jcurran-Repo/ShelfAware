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
    public static IEnumerable<Product> EdibleInStock(IEnumerable<Product> products, DateOnly today) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            ReplenishmentPredictor.Predict(p, today).Status != PredictionStatus.Overdue);

    /// <summary>The other side of the same rule: tracked, edible products the engine thinks you've RUN OUT
    /// of. These are exactly the items <see cref="EdibleInStock"/> silently drops — surfaced so a recipe
    /// row can say "you may still have this, it's just due for a re-buy" instead of a bare red mark.</summary>
    public static IEnumerable<Product> EdibleOutOfStock(IEnumerable<Product> products, DateOnly today) =>
        products.Where(p =>
            p.IsTracked &&
            p.Category.IsEdible() &&
            ReplenishmentPredictor.Predict(p, today).Status == PredictionStatus.Overdue);
}
