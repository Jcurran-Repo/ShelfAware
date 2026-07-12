namespace ShelfAware.Core.Shopping;

/// <summary>
/// Average confirmed unit price per product, keyed by size bucket, falling back to the product's
/// overall average when the asked-for size has no priced line. THE shared price lookup for the
/// shopping surfaces (Products grid, Grocery List, spend forecast) so they all price the
/// recommended (dominant) size the same way — sizes group via <see cref="PriceSeries.Bucket"/>,
/// so the loose/"each" spellings fold together and per-each produce matches a null recommended
/// size. Pure C#: callers fetch the (product, size, price) observations and pass them in.
/// </summary>
public sealed class ProductPriceIndex
{
    private readonly Dictionary<(int ProductId, string SizeBucket), decimal> _bySize;
    private readonly Dictionary<int, decimal> _overall;

    public ProductPriceIndex(IEnumerable<(int ProductId, string? Size, decimal UnitPrice)> observations)
    {
        var all = observations.ToList();
        _bySize = all
            .GroupBy(o => (o.ProductId, Bucket: PriceSeries.Bucket(o.Size)))
            .ToDictionary(g => g.Key, g => g.Average(o => o.UnitPrice));
        _overall = all
            .GroupBy(o => o.ProductId)
            .ToDictionary(g => g.Key, g => g.Average(o => o.UnitPrice));
    }

    /// <summary>The product's average price in <paramref name="size"/>'s bucket; the overall
    /// average across all sizes when that bucket has no priced line; null when the product has
    /// no priced line at all.</summary>
    public decimal? PriceFor(int productId, string? size)
    {
        if (_bySize.TryGetValue((productId, PriceSeries.Bucket(size)), out var sized)) return sized;
        return _overall.TryGetValue(productId, out var overall) ? overall : null;
    }
}
