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

    [Theory]
    [InlineData(6, "6 oz")]        // six 6-oz yogurts — the number matches, but oz is a MEASURE
    [InlineData(12, "12 oz")]      // twelve 12-oz cans
    [InlineData(5, "5.3 oz")]      // five 5.3-oz cups (leading count 5 = quantity 5)
    [InlineData(5, "5 lb")]        // five 5-lb flour bags
    [InlineData(12, "12 fl oz")]   // "fl oz" — both tokens are measures
    [InlineData(16, "16 g")]
    [InlineData(12, "12oz")]       // no space — the letter run "oz" is still a measure
    [InlineData(5, "5lb")]
    [InlineData(12, "12floz")]
    public void A_measure_size_whose_number_matches_the_quantity_is_NOT_flagged(int quantity, string size)
    {
        // The false positive the review caught: buying N of a per-unit weight/volume item whose size
        // number happens to equal N is an ordinary multi-buy, not a pack-count misread. Only a COUNT
        // size (ct/pk/roll/eggs/bare number) matching the quantity points to the leak.
        Assert.Equal(QuantityFlag.None, QuantityAnomaly.Check(quantity, size, []));
    }

    [Theory]
    [InlineData(12, "12 ct")]
    [InlineData(24, "24 pk")]
    [InlineData(18, "18 eggs")]
    [InlineData(6, "6 Mega Roll")]
    [InlineData(12, "12")]                 // a bare number is a count, not a measure
    [InlineData(24, "24pk")]               // no space — still a count
    [InlineData(24, "24 pk 12 fl oz")]     // COMPOUND multipack: a pack token wins over the trailing measure
    [InlineData(12, "12 pk 16.9 oz")]      // a case of water
    [InlineData(6, "6 pack 16 oz")]        // a 6-pack of cans
    [InlineData(12, "12 x 12 oz")]         // "12 x ..." — the leading number is the pack count
    public void A_count_size_matching_the_quantity_still_flags(int quantity, string size)
    {
        // Guard the gating didn't over-reach: the real pack-count shapes must still fire — including the
        // compound beverage/canned multipacks (the most common packs, and the ones a naive "measure token
        // anywhere → not a count" rule wrongly let slip past the flag).
        Assert.Equal(QuantityFlag.SizeMatchesQuantity, QuantityAnomaly.Check(quantity, size, []));
    }

    [Fact]
    public void A_measure_sized_product_bought_in_multiples_with_the_size_dropped_is_NOT_flagged()
    {
        // The same class in the missing-size tell: an item that USUALLY carries a MEASURE size (16 oz),
        // bought several times with the size not captured this once, is a legit multi-buy — not a pack
        // whose count leaked. Only a product that usually carries a COUNT size raises the flag.
        Assert.Equal(QuantityFlag.None,
            QuantityAnomaly.Check(6, null, ["16 oz", "16 oz", "16 oz"]));
    }

    [Fact]
    public void A_stock_up_of_a_pack_item_is_NOT_flagged_when_the_quantity_is_not_a_usual_pack_count()
    {
        // FOUR cartons of a usually-"12 ct" product, the size not captured this time — a genuine stock-up,
        // not a "4-pack". The quantity (4) matches no pack count this product is sold in (12), so the
        // missing-size tell must stay quiet: it fires only when the quantity IS a usual pack count.
        Assert.Equal(QuantityFlag.None,
            QuantityAnomaly.Check(4, null, ["12 ct", "12 ct", "12 ct"]));
        // …but the same shape at the usual pack count (12) IS the leak, and fires.
        Assert.Equal(QuantityFlag.MissingUsualSize,
            QuantityAnomaly.Check(12, null, ["12 ct", "12 ct", "12 ct"]));
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
