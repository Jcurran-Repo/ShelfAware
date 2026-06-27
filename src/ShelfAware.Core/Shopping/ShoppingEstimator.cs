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

    public decimal TypicalQuantity { get; init; } = 1m;
    /// <summary>Unit price supplied by the caller (avg of confirmed receipt lines), or null when unknown.</summary>
    public decimal? UnitPrice { get; init; }
    public decimal? ExpectedCost { get; init; }
}

public static class ShoppingEstimator
{
    public static ProductEstimate For(Product product, PredictionResult prediction, DateOnly today, decimal? unitPrice)
    {
        var purchaseDates = product.Purchases.Select(p => p.PurchasedAt).ToList();
        var quantities = product.Purchases.Select(p => p.Quantity).Where(q => q > 0).ToList();
        var typicalQuantity = quantities.Count > 0 ? Median(quantities) : 1m;

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
            UnitPrice = unitPrice,
            ExpectedCost = unitPrice is { } price ? price * typicalQuantity : null,
        };
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2m;
    }
}
