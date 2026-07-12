using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class ProductPriceIndexTests
{
    [Fact]
    public void Null_size_lines_price_an_each_recommendation()
    {
        // The drift this class exists to prevent: extraction writes loose produce as null size but
        // the predictor recommends "Each" — a raw string key puts them in different buckets and the
        // lookup silently misses. They're one pricing basis, so the sized average must be found.
        var index = new ProductPriceIndex([(1, null, 0.59m), (1, "each", 0.61m)]);

        Assert.Equal(0.60m, index.PriceFor(1, "Each"));
        Assert.Equal(0.60m, index.PriceFor(1, null));
    }

    [Fact]
    public void Recommended_size_prices_its_own_bucket_not_a_blend()
    {
        // Half-gallon vs gallon milk: estimate the size we actually tell the user to buy.
        var index = new ProductPriceIndex([(1, "1 gal", 3.49m), (1, "1 gal", 3.59m), (1, "64 fl oz", 2.29m)]);

        Assert.Equal(3.54m, index.PriceFor(1, " 1 GAL "));
        Assert.Equal(2.29m, index.PriceFor(1, "64 fl oz"));
    }

    [Fact]
    public void Unpriced_size_falls_back_to_the_overall_average()
    {
        // A size we've never seen a price for still gets an estimate — a blend beats a blank.
        var index = new ProductPriceIndex([(1, "12 oz", 2.00m), (1, "28 oz", 4.00m)]);

        Assert.Equal(3.00m, index.PriceFor(1, "6 oz"));
    }

    [Fact]
    public void Unknown_product_has_no_price()
    {
        var index = new ProductPriceIndex([(1, "12 oz", 2.00m)]);

        Assert.Null(index.PriceFor(2, "12 oz"));
    }
}
