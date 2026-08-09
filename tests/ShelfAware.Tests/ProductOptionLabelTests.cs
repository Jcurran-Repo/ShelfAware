using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

/// <summary>
/// The ONE phrasing rule for a twin product's dropdown option, shared by the receipt review grid and the
/// census grid. It exists so the two dropdowns can't drift apart — which is why the exact wording of each
/// branch is pinned here rather than per page.
/// </summary>
public class ProductOptionLabelTests
{
    [Fact]
    public void A_live_count_reads_as_on_hand()
    {
        Assert.Equal("Sardines — 4 cans on hand",
            ProductOptionLabel.ForTwin("Sardines", 4m, counting: true, "cans"));
    }

    [Fact]
    public void A_dormant_count_is_history_not_current_stock()
    {
        Assert.Equal("Sardines — had 4 cans, counting stopped",
            ProductOptionLabel.ForTwin("Sardines", 4m, counting: false, "cans"));
    }

    [Fact]
    public void A_never_counted_twin_says_so()
    {
        // `counting` is irrelevant without a number — TrackQuantity can be true on a product that has
        // no stored count yet, and the label must not imply one.
        Assert.Equal("Sardines — not counted",
            ProductOptionLabel.ForTwin("Sardines", null, counting: true, "cans"));
    }

    [Fact]
    public void The_quantity_is_phrased_by_QuantityFormat()
    {
        // One rule for quantity phrasing app-wide: unitless counts stay bare, exactly-1 plurals singularize.
        Assert.Equal("Sardines — 4 on hand", ProductOptionLabel.ForTwin("Sardines", 4m, true, null));
        Assert.Equal("Sardines — 1 can on hand", ProductOptionLabel.ForTwin("Sardines", 1m, true, "cans"));
    }
}
