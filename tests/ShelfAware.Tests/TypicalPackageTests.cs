using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class TypicalPackageTests
{
    [Fact]
    public void A_counted_item_bought_one_at_a_time_is_one()
    {
        Assert.Equal(1m, TypicalPackage.Of([1m, 1m, 1m]));
    }

    [Fact]
    public void A_weight_item_deducts_the_pack_this_household_actually_buys()
    {
        // The rule's whole point: 1 lb would be arbitrary — a pound is not a unit of anything about how
        // this household buys. Beef arriving in ~1.24 lb packs deducts 1.24.
        Assert.Equal(1.24m, TypicalPackage.Of([1.18m, 1.24m, 1.31m]));
    }

    [Fact]
    public void An_even_count_takes_the_midpoint()
    {
        Assert.Equal(1.25m, TypicalPackage.Of([1.2m, 1.3m]));
    }

    [Fact]
    public void Median_not_mode_because_weights_rarely_repeat()
    {
        // Mode is undefined here — every value occurs once. Median still answers.
        Assert.Equal(2.34m, TypicalPackage.Of([2.31m, 2.34m, 2.39m]));
    }

    [Fact]
    public void An_outlier_bulk_trip_does_not_drag_the_package_size()
    {
        // Six at once on a one-at-a-time item is a stock-up, not a bigger package. The median ignores it;
        // a mean would have claimed a package of ~1.8.
        Assert.Equal(1m, TypicalPackage.Of([1m, 1m, 1m, 1m, 6m]));
    }

    [Fact]
    public void No_history_falls_back_to_one()
    {
        Assert.Equal(1m, TypicalPackage.Of([]));
    }

    [Fact]
    public void Non_positive_quantities_are_noise_not_packages()
    {
        Assert.Equal(2m, TypicalPackage.Of([0m, -1m, 2m]));
        Assert.Equal(1m, TypicalPackage.Of([0m, 0m])); // nothing usable left → the fallback
    }
}
