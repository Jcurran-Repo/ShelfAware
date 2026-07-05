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
}
