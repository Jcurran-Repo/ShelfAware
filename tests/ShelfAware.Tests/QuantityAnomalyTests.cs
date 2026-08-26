using ShelfAware.Core.Domain;
using ShelfAware.Core.Ingest;

namespace ShelfAware.Tests;

public class QuantityAnomalyTests
{
    // The bug this exists for: a 12-pack of toilet paper, normally bought as one package with a size,
    // extracted as quantity 12.

    [Fact]
    public void The_pack_count_in_the_size_matching_the_quantity_is_flagged()
    {
        // "12 ct" AND quantity 12: the count is in both fields — a misread. No history needed.
        Assert.Equal(QuantityFlag.SizeMatchesQuantity,
            QuantityAnomaly.Check(12, "12 ct", []));
        Assert.Equal(QuantityFlag.SizeMatchesQuantity,
            QuantityAnomaly.Check(6, "6 Mega Roll", ["6 Mega Roll", "6 Mega Roll"]));
    }

    [Fact]
    public void A_count_shaped_quantity_with_no_size_is_flagged_when_the_product_usually_has_one()
    {
        // Toilet paper's real shape: every prior buy carried a size, this one lost it (the count leaked
        // into quantity). Jordan's tell.
        Assert.Equal(QuantityFlag.MissingUsualSize,
            QuantityAnomaly.Check(12, null, ["12 rolls", "12 rolls", "12 rolls"]));
        Assert.Equal(QuantityFlag.MissingUsualSize,
            QuantityAnomaly.Check(12, "  ", ["24 ct", "12 ct"])); // blank size counts as missing
    }

    [Fact]
    public void A_genuine_stock_up_is_NOT_flagged()
    {
        // Buy twelve when you usually buy one, as twelve SINGLE items: quantity 12, size is the single
        // item's size (unchanged), which doesn't equal 12. The engine is built to honour this stock-up
        // (uncapped stock-up factor) — flagging it would harass every bulk buyer.
        Assert.Equal(QuantityFlag.None,
            QuantityAnomaly.Check(12, "each", ["each", "each", "each"]));
        Assert.Equal(QuantityFlag.None,
            QuantityAnomaly.Check(6, "16 oz", ["16 oz", "16 oz"])); // 6 packages of a 16 oz item
    }

    [Fact]
    public void A_missing_size_is_NOT_flagged_when_the_product_never_has_a_size()
    {
        // Loose produce bought by the dozen: no size is normal, and "12 limes" is a real quantity, not
        // a misread. Without a usual size there's no evidence the count came from a pack.
        Assert.Equal(QuantityFlag.None,
            QuantityAnomaly.Check(12, null, [null, null, "each"]));
    }

    [Fact]
    public void A_brand_new_product_is_flagged_only_by_the_size_match()
    {
        // No history: the missing-usual-size tell can't fire (nothing says it usually has a size), but
        // the count-in-both-fields tell still can.
        Assert.Equal(QuantityFlag.SizeMatchesQuantity, QuantityAnomaly.Check(12, "12 pack", []));
        Assert.Equal(QuantityFlag.None, QuantityAnomaly.Check(12, null, []));
    }

    [Theory]
    [InlineData(1)]  // one package — nothing to misread
    [InlineData(2)]  // ordinary two-of-something
    [InlineData(3)]  // still below the floor
    public void Small_quantities_are_never_flagged(int quantity)
    {
        // Even with a matching size, a small count is an ordinary multi-buy, not a pack size worth
        // second-guessing.
        Assert.Equal(QuantityFlag.None, QuantityAnomaly.Check(quantity, $"{quantity} ct", ["1 ct"]));
    }

    [Fact]
    public void A_fractional_quantity_is_never_flagged()
    {
        // A weight-priced item (2.31 lb → quantity 2.31, size "lb") is never a pack count.
        Assert.Equal(QuantityFlag.None, QuantityAnomaly.Check(2.31m, "lb", ["lb", "lb"]));
        // Even a whole-looking weight with a matching-ish size stays out via the fractional guard.
        Assert.Equal(QuantityFlag.None, QuantityAnomaly.Check(12.5m, "12 oz", ["12 oz"]));
    }

    [Fact]
    public void Describe_words_each_flag_as_a_soft_question_and_says_nothing_for_None()
    {
        var sizeMatch = QuantityAnomaly.Describe(QuantityFlag.SizeMatchesQuantity, 12, "12 ct");
        Assert.Contains("12", sizeMatch);
        Assert.Contains("12 ct", sizeMatch);
        Assert.EndsWith("?", sizeMatch); // a question, not an accusation

        var missing = QuantityAnomaly.Describe(QuantityFlag.MissingUsualSize, 12, null);
        Assert.Contains("12-pack", missing);
        Assert.EndsWith("?", missing);

        Assert.Equal("", QuantityAnomaly.Describe(QuantityFlag.None, 12, "12 ct"));
    }

    [Theory]
    [InlineData("12 ct", 12)]
    [InlineData("6 Mega Roll", 6)]
    [InlineData("1 gal", 1)]
    [InlineData("24pk", 24)]
    [InlineData("lb", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("half dozen", null)]
    public void LeadingCount_reads_the_first_number(string size, int? expected)
    {
        Assert.Equal(expected, QuantityAnomaly.LeadingCount(size));
    }
}
