using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class SizeFormatTests
{
    [Theory]
    [InlineData("12 Count", "12 count")]
    [InlineData("12 count", "12 count")]
    [InlineData("12  COUNT", "12 count")]
    [InlineData("  1 GAL  ", "1 gal")]
    [InlineData("16 Fl Oz", "16 fl oz")]
    public void Normalize_CollapsesCasingAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, SizeFormat.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsNull_ForBlankInput(string? input)
    {
        Assert.Null(SizeFormat.Normalize(input));
    }

    [Fact]
    public void Normalize_MakesTrivialCasingVariantsEqual()
    {
        // The whole point: "12 Count" and "12 count" must display as one label.
        Assert.Equal(SizeFormat.Normalize("12 Count"), SizeFormat.Normalize("12 count"));
    }
}
