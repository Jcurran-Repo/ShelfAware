using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Core.Shopping;

/// <summary>
/// Per-product shopping estimate combining the (price-free) timing prediction with quantity and a
/// unit price. Pure C#: the unit price is passed IN — the Web layer fetches it from EF-stored
/// ReceiptLine prices and supplies it here, so Core stays EF-free (DESIGN.md §3) and the prediction
/// engine stays pure timing statistics (§1/§6). Shared by the Products grid and the Grocery List.
/// </summary>
public record ProductEstimate
{
    public required int ProductId { get; init; }
    public required string Name { get; init; }
    public required Category Category { get; init; }
    public required PredictionStatus Status { get; init; }
    public string Basis { get; init; } = "";

    public DateOnly? LastPurchased { get; init; }
    public DateOnly? NextBuyDate { get; init; }
    /// <summary>Days until the predicted run-out; negative means overdue. Null when status is Unknown.</summary>
    public int? DaysUntil { get; init; }

    /// <summary>The statistical typical (median) purchase quantity — kept for analysis. For a shopping
    /// number, prefer <see cref="RecommendedQuantity"/>.</summary>
    public decimal TypicalQuantity { get; init; } = 1m;
    /// <summary>How many to actually buy: the typical quantity rounded UP for whole-unit (count) items,
    /// so a "1.5" median (bought 1 then 2) reads as 2 — you can't buy half a box. Left fractional for
    /// genuinely weight/volume-priced items (any purchase quantity is non-integer, e.g. "2.31 lb").
    /// Drives the displayed buy-quantity and the est. cost.</summary>
    public decimal RecommendedQuantity { get; init; } = 1m;
    /// <summary>Unit price supplied by the caller (avg of confirmed receipt lines for the recommended
    /// size), or null when unknown.</summary>
    public decimal? UnitPrice { get; init; }
    public decimal? ExpectedCost { get; init; }

    /// <summary>Display-normalized package size to recommend buying (the dominant size), or null.</summary>
    public string? RecommendedSize { get; init; }
    /// <summary>Most-bought brand for this item, with a "+N" hint when bought across several brands
    /// (e.g. "Great Value +2"); null when no purchase carries a brand.</summary>
    public string? UsualBrand { get; init; }
    /// <summary>Most-bought variety, same shape as <see cref="UsualBrand"/> ("Gala +1" = usually
    /// gala, other kinds too); null when no purchase carries a variety. Shown wherever the brand is —
    /// a variety you can't see is a variety you can't shop for.</summary>
    public string? UsualVariety { get; init; }
    /// <summary>Every brand bought as "Name ×buys", most-bought first — the breakdown behind
    /// <see cref="UsualBrand"/>'s "+N", for the tap-for-detail hint on the shopping surfaces.</summary>
    public IReadOnlyList<string> BrandsBought { get; init; } = [];
    /// <summary>Every variety bought, same shape as <see cref="BrandsBought"/> — so "fresh Envy and
    /// Cosmic Crisp" is one tap away on the list, not a trip to the product page.</summary>
    public IReadOnlyList<string> VarietiesBought { get; init; } = [];
    /// <summary>The product's descriptive tags, for display on the shopping list.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public static class ShoppingEstimator
{
    public static ProductEstimate For(Product product, PredictionResult prediction, DateOnly today, decimal? unitPrice)
    {
        var purchaseDates = product.Purchases.Select(p => p.PurchasedAt).ToList();

        // A typical buy is a TRIP's worth, not a receipt line's worth: 3 Gala plus 3 Honeycrisp on one
        // receipt is a six-apple trip, and the list should say 6. Same-day quantities sum (the predictor's
        // stock-up factor already reads trips this way), then the median runs over trips.
        var tripTotals = product.Purchases
            .Where(p => p.Quantity > 0)
            .GroupBy(p => p.PurchasedAt)
            .Select(g => g.Sum(p => p.Quantity))
            .ToList();
        var typicalQuantity = tripTotals.Count > 0 ? Median(tripTotals) : 1m;

        // Round the buy-quantity UP for whole-unit (count) items so a median like 1.5 reads as "buy 2".
        // Genuinely weight/volume-priced items (any trip total is fractional) stay precise.
        var allWhole = tripTotals.All(q => q == decimal.Truncate(q));
        var recommendedQuantity = allWhole ? Math.Ceiling(typicalQuantity) : typicalQuantity;

        return new ProductEstimate
        {
            ProductId = product.Id,
            Name = product.Name,
            Category = product.Category,
            Status = prediction.Status,
            Basis = prediction.Basis,
            LastPurchased = purchaseDates.Count > 0 ? purchaseDates.Max() : null,
            NextBuyDate = prediction.DueDate,
            DaysUntil = prediction.DueDate is { } due ? due.DayNumber - today.DayNumber : null,
            TypicalQuantity = typicalQuantity,
            RecommendedQuantity = recommendedQuantity,
            UnitPrice = unitPrice,
            ExpectedCost = unitPrice is { } price ? price * recommendedQuantity : null,
            RecommendedSize = SizeFormat.Normalize(prediction.RecommendedSize),
            UsualBrand = UsualBrandOf(product.Purchases),
            UsualVariety = UsualVarietyOf(product.Purchases),
            BrandsBought = BrandsBoughtOf(product.Purchases),
            VarietiesBought = VarietiesBoughtOf(product.Purchases),
            Tags = product.Tags.Select(t => t.Value).OrderBy(t => t).ToList(),
        };
    }

    /// <summary>Most-bought brand across an item's purchases, with a "+N" hint when bought across several
    /// brands (e.g. "Great Value +2"); null when no purchase carries a brand. Shared by the Products grid,
    /// Grocery List, and dashboard so the "usual brand" reads identically everywhere.</summary>
    public static string? UsualBrandOf(IEnumerable<PurchaseEvent> purchases) => UsualOf(purchases, pe => pe.Brand);

    /// <summary>Most-bought variety, same rules and "+N" shape as <see cref="UsualBrandOf"/>.</summary>
    public static string? UsualVarietyOf(IEnumerable<PurchaseEvent> purchases) => UsualOf(purchases, pe => pe.Variety);

    /// <summary>Every brand bought as "Name ×buys", most-bought first (ties alphabetical) — what the
    /// "+N" hint summarizes; the shopping surfaces show it on tap.</summary>
    public static IReadOnlyList<string> BrandsBoughtOf(IEnumerable<PurchaseEvent> purchases) =>
        [.. GroupsOf(purchases, pe => pe.Brand).Select(g => $"{g.Key} ×{g.Count()}")];

    /// <summary>Every variety bought, same shape as <see cref="BrandsBoughtOf"/>.</summary>
    public static IReadOnlyList<string> VarietiesBoughtOf(IEnumerable<PurchaseEvent> purchases) =>
        [.. GroupsOf(purchases, pe => pe.Variety).Select(g => $"{g.Key} ×{g.Count()}")];

    private static string? UsualOf(IEnumerable<PurchaseEvent> purchases, Func<PurchaseEvent, string?> label)
    {
        var groups = GroupsOf(purchases, label);
        if (groups.Count == 0) return null;
        return groups.Count == 1 ? groups[0].Key : $"{groups[0].Key} +{groups.Count - 1}";
    }

    // Case-insensitive grouping (first-seen casing displays): a review-edited "gala" next to an
    // extracted "Gala" is one variety, not a "+1" — and the count here must agree with Product
    // Detail's breakdown sections, which fold case the same way.
    private static List<IGrouping<string, PurchaseEvent>> GroupsOf(
        IEnumerable<PurchaseEvent> purchases, Func<PurchaseEvent, string?> label) =>
        [.. purchases
            .Where(pe => !string.IsNullOrWhiteSpace(label(pe)))
            .GroupBy(pe => label(pe)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)];

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2m;
    }
}
