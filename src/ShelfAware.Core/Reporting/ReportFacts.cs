namespace ShelfAware.Core.Reporting;

using ShelfAware.Core.Domain;

/// <summary>One purchase, flattened for reporting — the Web layer joins EF rows into these so the
/// engine stays EF-free (the same seam shape as ShoppingEstimator's inputs).</summary>
/// <param name="Price">What one unit cost this time: the purchase's own receipt-line price when it has
/// one, the product's size-aware index estimate when it doesn't, null when nothing knows — an unpriced
/// purchase counts for quantity/count metrics but is skipped (and disclosed) for spend.</param>
/// <param name="PaidUnitPrice">The receipt-line price ONLY — never an estimate. The UnitPrice metric
/// charts what was actually paid; feeding it estimates would draw a flat line of its own average.</param>
/// <param name="InDominantSizeBucket">Whether this purchase's size is in the product's most-bought
/// size bucket (stamped by the caller via PriceSeries.Dominant). The UnitPrice metric only reads
/// these, the Trends like-with-like rule.</param>
public sealed record PurchaseFact(
    DateOnly Date,
    int ProductId,
    string ProductName,
    Category Category,
    decimal Quantity,
    decimal? Price,
    decimal? PaidUnitPrice,
    bool InDominantSizeBucket,
    IReadOnlyList<string> Tags);

/// <summary>One logged meal, flattened. <paramref name="CaloriesPerServing"/> is the recipe's
/// LLM-ballpark estimate, null when the recipe has none — such meals count for MealsCooked and are
/// skipped (and disclosed) for Calories.</summary>
public sealed record MealFact(
    DateOnly Date,
    int RecipeId,
    string RecipeName,
    int? CaloriesPerServing);
