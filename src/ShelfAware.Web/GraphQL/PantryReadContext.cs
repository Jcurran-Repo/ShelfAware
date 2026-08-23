using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Shopping;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.GraphQL;

/// <summary>Per-request shared read state for the computed fields. "Today", the expiration setting, and
/// the price index are each derived ONCE per GraphQL request (not once per product), and a product's
/// prediction is computed ONCE and memoized. Scoped — Hot Chocolate resolves it from the request scope,
/// and a GraphQL request is one scope. Thread-safe on purpose: Hot Chocolate may resolve fields in
/// parallel, so the <see cref="Lazy{T}"/> factories run exactly once and the per-product memo stores a
/// pure result.
///
/// ⚠️ This is the API's "one prediction, one story" guarantee: the <c>prediction</c> field and the
/// <c>estimate</c> field both call <see cref="PredictAsync"/>, which memoizes per product id, so they
/// return the SAME <see cref="PredictionResult"/> — the estimate beside a prediction can never disagree
/// with it. The flags match the product SURFACES exactly (<c>honorQuantity: true</c>,
/// <c>honorExpirations</c> = the household's TrackExpirationDates setting), NOT the reports/backtest
/// sites (which pass <c>honorQuantity: false</c>).</summary>
public sealed class PantryReadContext(IHouseholdDbFactory dbFactory, IAppSettings settings)
{
    private readonly Lazy<Task<bool>> _honorExpirations = new(() => settings.GetTrackExpirationDatesAsync());
    private readonly Lazy<Task<ProductPriceIndex>> _priceIndex = new(() => BuildPriceIndexAsync(dbFactory));
    private readonly ConcurrentDictionary<int, PredictionResult> _predictions = new();

    /// <summary>Server-local, matching every other "today" in the app (DateTime.Today). Captured once so
    /// every field in the request shares one calendar day.</summary>
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);

    public Task<bool> HonorExpirationsAsync() => _honorExpirations.Value;

    public Task<ProductPriceIndex> PriceIndexAsync() => _priceIndex.Value;

    /// <summary>The product's prediction with the product-surface flags — computed once per product and
    /// memoized, so the <c>prediction</c> and <c>estimate</c> fields share one result. Keyed by product
    /// id, which is safe because a request holds one instance per product; the pure factory is idempotent
    /// even if it races.</summary>
    public async Task<PredictionResult> PredictAsync(Product product)
    {
        var honor = await HonorExpirationsAsync();
        return _predictions.GetOrAdd(product.Id,
            _ => ReplenishmentPredictor.Predict(product, Today, honor, honorQuantity: true));
    }

    private static async Task<ProductPriceIndex> BuildPriceIndexAsync(IHouseholdDbFactory dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Match the product surfaces exactly (Products/GroceryList/ProductDetail): every priced line with
        // a resolved product. A pending-review line has a null ProductId, so this is effectively the
        // confirmed history the grid prices from. Household-filtered like every other read here.
        var lines = await db.ReceiptLines
            .AsNoTracking()
            .Where(l => l.ProductId != null && l.UnitPrice != null)
            .Select(l => new { Id = l.ProductId!.Value, l.Size, Price = l.UnitPrice!.Value })
            .ToListAsync();
        return new ProductPriceIndex(lines.Select(x => (x.Id, x.Size, x.Price)));
    }
}
