using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class TypicalPackageTests
{
    private const string Weighed = "lb"; // a declared unit is what makes an item a WEIGHT item (§13.1)

    [Fact]
    public void A_counted_item_bought_one_at_a_time_is_one()
    {
        Assert.Equal(1m, TypicalPackage.Of(null, [1m, 1m, 1m]));
    }

    [Fact]
    public void A_counted_item_bought_SIX_at_a_time_is_still_one()
    {
        // The one that mattered: a receipt line reading "Beef Chuck Roast × 6" is one purchase OF six,
        // not one purchase of a six-pack. Taking the median here would charge SIX roasts for cooking one
        // — emptying the count in a single meal and putting the item straight back on the grocery list,
        // for exactly the bulk-buying household §13 exists to serve.
        Assert.Equal(1m, TypicalPackage.Of(null, [6m, 6m, 6m]));
    }

    [Fact]
    public void A_weight_item_deducts_the_pack_this_household_actually_buys()
    {
        // The rule's whole point: 1 lb would be arbitrary — a pound is not a unit of anything about how
        // this household buys. Beef arriving in ~1.24 lb packs deducts 1.24.
        Assert.Equal(1.24m, TypicalPackage.Of(Weighed, [1.18m, 1.24m, 1.31m]));
    }

    [Fact]
    public void An_even_count_takes_the_midpoint()
    {
        Assert.Equal(1.25m, TypicalPackage.Of(Weighed, [1.2m, 1.3m]));
    }

    [Fact]
    public void Median_not_mode_because_weights_rarely_repeat()
    {
        // Mode is undefined here — every value occurs once. Median still answers.
        Assert.Equal(2.34m, TypicalPackage.Of(Weighed, [2.31m, 2.34m, 2.39m]));
    }

    [Fact]
    public void An_outlier_bulk_trip_does_not_drag_the_package_size()
    {
        // Six at once on a one-at-a-time item is a stock-up, not a bigger package. The median ignores it;
        // a mean would have claimed a package of ~1.8.
        Assert.Equal(1m, TypicalPackage.Of(Weighed, [1m, 1m, 1m, 1m, 6m]));
    }

    [Fact]
    public void A_blank_unit_counts_as_no_unit()
    {
        // Whitespace is how a unit field ends up "set" without saying anything.
        Assert.Equal(1m, TypicalPackage.Of("   ", [6m, 6m]));
    }

    [Fact]
    public void No_history_falls_back_to_one()
    {
        Assert.Equal(1m, TypicalPackage.Of(Weighed, []));
    }

    [Fact]
    public void Non_positive_quantities_are_noise_not_packages()
    {
        Assert.Equal(2m, TypicalPackage.Of(Weighed, [0m, -1m, 2m]));
        Assert.Equal(1m, TypicalPackage.Of(Weighed, [0m, 0m])); // nothing usable left → the fallback
    }
}
