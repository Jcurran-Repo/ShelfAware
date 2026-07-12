using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class SizeBucketTests
{
    [Fact]
    public void Each_family_spellings_collapse_to_one_bucket()
    {
        // Loose produce is priced per unit however many you grab; extraction writes the size as
        // null / "each" / "EA" / "1 ct" inconsistently. All one buying basis, so all one bucket —
        // for prices AND for the predictor's dominant-size cadence.
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key(null));
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key(""));
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key("each"));
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key("EA"));
        Assert.Equal(SizeBucket.EachKey, SizeBucket.Key(" 1 ct "));
    }

    [Fact]
    public void Real_sizes_group_by_trimmed_lowercased_text_only()
    {
        Assert.Equal("3 lb bag", SizeBucket.Key(" 3 LB Bag "));
        Assert.NotEqual(SizeBucket.Key("1 gal"), SizeBucket.Key("64 fl oz")); // no unit arithmetic
    }
}
