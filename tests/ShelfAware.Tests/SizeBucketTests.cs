using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class SizeBucketTests
{
    [Fact]
    public void Null_collapses_to_the_each_bucket()
    {
        // Loose produce is priced per unit however many you grab; extraction writes the size as
        // null / "each" / "EA" / "1 ct" inconsistently. All one buying basis, so all one bucket —
        // for prices AND for the predictor's dominant-size cadence. The literal "each" is asserted
        // (not SizeBucket.EachKey) so a mutation of the EachKey constant itself is caught.
        Assert.Equal("each", SizeBucket.Key(null));
    }

    // Every each-family spelling collapses to the one bucket — each is a live mutation target now that
    // the spellings live in the method body, so each needs its own case, incl. the case/trim folding.
    [Theory]
    [InlineData("")]
    [InlineData("each")]
    [InlineData("ea")]
    [InlineData("ea.")]
    [InlineData("per each")]
    [InlineData("1 each")]
    [InlineData("1 ct")]
    [InlineData("1ct")]
    [InlineData("loose")]
    [InlineData("single")]
    [InlineData("EA")]      // case-folded
    [InlineData(" 1 ct ")]  // trimmed
    [InlineData("Loose")]   // case-folded
    public void Each_family_spellings_all_collapse_to_the_each_bucket(string size)
    {
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key(size));
    }

    [Fact]
    public void Real_sizes_group_by_trimmed_lowercased_text_only()
    {
        Assert.Equal("3 lb bag", SizeBucket.Key(" 3 LB Bag "));
        Assert.NotEqual(SizeBucket.Key("1 gal"), SizeBucket.Key("64 fl oz")); // no unit arithmetic
        Assert.Equal("gallon", SizeBucket.Key("gallon")); // a non-each size is not the each bucket
    }
}
