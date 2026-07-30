using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

/// <summary>
/// What "one package" is, decided by the quantities themselves (DESIGN.md §13.3). The product's declared
/// unit is deliberately not an input — see <see cref="TypicalPackage"/> for why it was the wrong
/// discriminator twice over.
/// </summary>
public class TypicalPackageTests
{
    [Fact]
    public void A_counted_item_bought_one_at_a_time_is_one()
    {
        Assert.Equal(1m, TypicalPackage.Of([1m, 1m, 1m]));
    }

    [Fact]
    public void A_counted_item_bought_SIX_at_a_time_is_still_one()
    {
        // A receipt line reading "Beef Chuck Roast × 6" is one purchase OF six, not one purchase of a
        // six-pack. Taking the median here would charge six roasts for cooking one — emptying the count
        // in a single meal, which LIFTS suppression and puts the item straight back on the grocery list,
        // for exactly the bulk-buying household §13 exists to serve.
        Assert.Equal(1m, TypicalPackage.Of([6m, 6m, 6m]));
    }

    [Fact]
    public void A_weight_item_deducts_the_pack_this_household_actually_buys()
    {
        // The rule's whole point: 1 lb would be arbitrary — a pound is not a unit of anything about how
        // this household buys. Beef arriving in ~1.24 lb packs deducts 1.24. Note there is no unit
        // argument: the fractional quantities are what say this is a measure rather than a count.
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
        // Six at once on a one-at-a-time item is a stock-up, not a bigger package.
        Assert.Equal(1m, TypicalPackage.Of([1m, 1m, 1m, 1m, 6m]));
    }

    [Fact]
    public void One_odd_corrected_quantity_cannot_flip_a_counted_item_into_weight_mode()
    {
        // The median decides, not "any fractional value present" — so a single hand-corrected 1.5 among
        // whole counts leaves the item counted. This is why the rule reads the median.
        Assert.Equal(1m, TypicalPackage.Of([1m, 1m, 1.5m, 1m, 1m]));
    }

    [Fact]
    public void A_mostly_fractional_history_reads_as_a_measure()
    {
        // The mirror: once the middle of the distribution is fractional, this is a weight item.
        Assert.Equal(2.2m, TypicalPackage.Of([2m, 2.2m, 2.4m]));
    }

    [Fact]
    public void A_weight_item_whose_median_lands_whole_reads_as_counted()
    {
        // The residual limit, pinned so it's a known cost rather than a surprise: beef at exactly 2.00 lb
        // every time is indistinguishable from a count of 2 without a unit, and deducts 1. Continuous
        // weights essentially never do this, and the alternative was trusting a field nothing writes.
        Assert.Equal(1m, TypicalPackage.Of([2m, 2m, 2m]));
    }

    [Fact]
    public void No_history_falls_back_to_one()
    {
        Assert.Equal(1m, TypicalPackage.Of([]));
    }

    [Fact]
    public void Non_positive_quantities_are_noise_not_packages()
    {
        Assert.Equal(1m, TypicalPackage.Of([0m, -1m, 2m])); // median 2 → whole → counted
        Assert.Equal(1.5m, TypicalPackage.Of([0m, -1m, 1.5m])); // median 1.5 → a measure
        Assert.Equal(1m, TypicalPackage.Of([0m, 0m])); // nothing usable left → the fallback
    }
}
