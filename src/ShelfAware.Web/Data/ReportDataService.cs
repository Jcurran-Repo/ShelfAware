using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Reporting;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Web.Data;

/// <summary>Everything a report render needs, loaded once. Kept separate from the ReportFacts rows
/// so the builder UI can offer real choices (which products exist, which tags, how far back data
/// goes) without a second trip.</summary>
public sealed record ReportSourceData(
    IReadOnlyList<PurchaseFact> Purchases,
    IReadOnlyList<MealFact> Meals,
    IReadOnlyList<(int Id, string Name)> Products,
    IReadOnlyList<string> Tags,
    DateOnly? FirstPurchase);

/// <summary>
/// Joins the household's EF rows into the report engine's flat facts — the one place reporting
/// touches the database. Pricing mirrors the Trends page exactly: a purchase's own receipt-line
/// price is the paid truth, the size-aware index estimate fills spend gaps, and dominant-size
/// membership is stamped here (via the shared PriceSeries/SizeBucket) so the engine's UnitPrice
/// metric compares like with like without knowing what a "size" is.
/// </summary>
public sealed class ReportDataService(IHouseholdDbFactory dbFactory)
{
    public async Task<ReportSourceData> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var products = await db.Products.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.Category })
            .ToListAsync(ct);
        var productById = products.ToDictionary(p => p.Id);

        var tagsByProduct = (await db.ProductTags.AsNoTracking()
                .Select(t => new { t.ProductId, t.Value })
                .ToListAsync(ct))
            .GroupBy(t => t.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Value).ToList());

        // The priced observations, receipt-dated — the same base rows the Trends page reads.
        var lineData = await db.ReceiptLines.AsNoTracking()
            .Where(l => l.ProductId != null && l.UnitPrice != null)
            .Select(l => new
            {
                ProductId = l.ProductId!.Value,
                l.ReceiptId,
                l.Size,
                Price = l.UnitPrice!.Value,
                Date = l.Receipt!.PurchasedAt,
            })
            .ToListAsync(ct);

        var priceIndex = new ProductPriceIndex(lineData.Select(x => (x.ProductId, x.Size, x.Price)));
        var byReceiptProduct = lineData
            .GroupBy(x => (x.ReceiptId, x.ProductId))
            .ToDictionary(g => g.Key, g => g.Average(x => x.Price));
        var dominantKeyByProduct = lineData
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => PriceSeries.Dominant(g.Select(x => new PricePoint(x.Size, x.Date, x.Price)).ToList())!.SizeKey);

        var purchases = await db.PurchaseEvents.AsNoTracking().ToListAsync(ct);
        var purchaseFacts = new List<PurchaseFact>(purchases.Count);
        foreach (var pe in purchases)
        {
            if (!productById.TryGetValue(pe.ProductId, out var product)) continue;

            // The purchase's own receipt line is the exact paid price; the index is the estimate.
            decimal? paid = pe.ReceiptId is { } rid && byReceiptProduct.TryGetValue((rid, pe.ProductId), out var linePrice)
                ? linePrice : null;
            var inDominant = dominantKeyByProduct.TryGetValue(pe.ProductId, out var dominantKey)
                && SizeBucket.Key(pe.Size) == dominantKey;

            purchaseFacts.Add(new PurchaseFact(
                pe.PurchasedAt,
                pe.ProductId,
                product.Name,
                product.Category,
                pe.Quantity,
                paid ?? priceIndex.PriceFor(pe.ProductId, pe.Size),
                paid,
                inDominant,
                tagsByProduct.GetValueOrDefault(pe.ProductId) ?? []));
        }

        var mealFacts = (await db.MealEvents.AsNoTracking()
                .Join(db.Recipes,
                    m => m.RecipeId, r => r.Id,
                    (m, r) => new { m.AteAt, m.RecipeId, r.Name, r.EstimatedCaloriesPerServing })
                .ToListAsync(ct))
            .Select(m => new MealFact(m.AteAt, m.RecipeId, m.Name, m.EstimatedCaloriesPerServing))
            .ToList();

        return new ReportSourceData(
            purchaseFacts,
            mealFacts,
            products.OrderBy(p => p.Name).Select(p => (p.Id, p.Name)).ToList(),
            tagsByProduct.Values.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
            purchaseFacts.Count > 0 ? purchaseFacts.Min(f => f.Date) : null);
    }
}
