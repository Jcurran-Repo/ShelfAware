using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class QuantityFormatTests
{
    [Fact]
    public void A_declared_unit_is_carried_onto_the_number()
    {
        Assert.Equal("2.34 lb", QuantityFormat.Describe(2.34m, "lb"));
        Assert.Equal("4 ct", QuantityFormat.Describe(4m, "ct"));
    }

    [Fact]
    public void No_declared_unit_stays_a_bare_number_and_never_guesses_packages()
    {
        // The whole point: quantity carries no unit of its own, so 2.34 of a weight item is ONE package
        // weighing 2.34 lb. Rendering "2.34 packages" would be a confident lie where "2.34" is merely
        // incomplete, and most products have no unit set.
        Assert.Equal("2.34", QuantityFormat.Describe(2.34m, null));
        Assert.Equal("4", QuantityFormat.Describe(4m, ""));
        Assert.Equal("4", QuantityFormat.Describe(4m, "   "));
    }

    [Fact]
    public void Whole_counts_lose_their_trailing_zeros()
    {
        Assert.Equal("4", QuantityFormat.Describe(4.0m, null));
        Assert.Equal("10", QuantityFormat.Describe(10.00m, null));
    }

    [Fact]
    public void Two_decimals_survive_because_weights_need_them()
    {
        // The bug this pins: "0.#" rounds a 2.34 lb pack of beef to "2.3", losing precision on exactly
        // the items a unit label exists for. Two places, like the recommended-quantity displays.
        Assert.Equal("2.34 lb", QuantityFormat.Describe(2.34m, "lb"));
        Assert.Equal("2.35 lb", QuantityFormat.Describe(2.345m, "lb"));
    }

    [Fact]
    public void A_padded_unit_is_trimmed_so_the_spacing_is_ours()
    {
        Assert.Equal("3 gal", QuantityFormat.Describe(3m, "  gal  "));
    }

    [Fact]
    public void Exactly_one_of_a_plural_unit_reads_singular()
    {
        // Units are human-typed and the box invites plurals ("cans", "jars"), which read wrong beside
        // a 1 the moment the count gets there — "Typical buy: 1 cans" shipped exactly that way.
        Assert.Equal("1 can", QuantityFormat.Describe(1m, "cans"));
        Assert.Equal("1 jar", QuantityFormat.Describe(1m, "jars"));
        Assert.Equal("4 cans", QuantityFormat.Describe(4m, "cans"));
        Assert.Equal("1 can", QuantityFormat.Describe(1.0m, "cans")); // 1.0 is still exactly one
        Assert.Equal("1 Can", QuantityFormat.Describe(1m, "Cans")); // capitalization must not dodge it
        Assert.Equal("1 CAN", QuantityFormat.Describe(1m, "CANS"));
    }

    [Fact]
    public void Singularizing_never_touches_units_that_are_not_plurals()
    {
        Assert.Equal("1 lb", QuantityFormat.Describe(1m, "lb"));
        Assert.Equal("1 each", QuantityFormat.Describe(1m, "each"));
        Assert.Equal("1 glass", QuantityFormat.Describe(1m, "glass")); // trailing "ss" is a name, not a plural
        Assert.Equal("1.5 cans", QuantityFormat.Describe(1.5m, "cans")); // only exactly one
        Assert.Equal("1 s", QuantityFormat.Describe(1m, "s")); // a one-letter unit has nothing to trim
    }
}
