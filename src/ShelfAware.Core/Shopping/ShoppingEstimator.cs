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
    /// <summary>The product's descriptive tags, for display on the shopping list.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public static class ShoppingEstimator
{
    public static ProductEstimate For(Product product, PredictionResult prediction, DateOnly today, decimal? unitPrice)
    {
        var purchaseDates = product.Purchases.Select(p => p.PurchasedAt).ToList();
        var quantities = product.Purchases.Select(p => p.Quantity).Where(q => q > 0).ToList();
        var typicalQuantity = quantities.Count > 0 ? Median(quantities) : 1m;

        // Round the buy-quantity UP for whole-unit (count) items so a median like 1.5 reads as "buy 2".
        // Genuinely weight/volume-priced items (any purchase quantity is fractional) stay precise.
        var allWhole = quantities.All(q => q == decimal.Truncate(q));
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
            Tags = product.Tags.Select(t => t.Value).OrderBy(t => t).ToList(),
        };
    }

    /// <summary>Most-bought brand across an item's purchases, with a "+N" hint when bought across several
    /// brands (e.g. "Great Value +2"); null when no purchase carries a brand. Shared by the Products grid,
    /// Grocery List, and dashboard so the "usual brand" reads identically everywhere.</summary>
    public static string? UsualBrandOf(IEnumerable<PurchaseEvent> purchases)
    {
        var brands = purchases
            .Where(pe => !string.IsNullOrWhiteSpace(pe.Brand))
            .GroupBy(pe => pe.Brand!.Trim())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToList();
        if (brands.Count == 0) return null;
        return brands.Count == 1 ? brands[0].Key : $"{brands[0].Key} +{brands.Count - 1}";
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2m;
    }
}
