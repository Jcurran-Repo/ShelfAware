using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class NudgeMoodTests
{
    [Theory]
    [InlineData(0, NudgeMood.Fresh)]
    [InlineData(2, NudgeMood.Fresh)]     // still cheery just under the 3-day line
    [InlineData(3, NudgeMood.Deflating)] // boundary → deflating
    [InlineData(6, NudgeMood.Deflating)]
    [InlineData(7, NudgeMood.Nagging)]   // boundary → nagging
    [InlineData(13, NudgeMood.Nagging)]
    [InlineData(14, NudgeMood.Frazzled)] // boundary → peak Meeseeks
    [InlineData(40, NudgeMood.Frazzled)]
    public void Mood_degrades_with_age(int days, NudgeMood expected) =>
        Assert.Equal(expected, NudgeMoods.For(TimeSpan.FromDays(days)));

    [Fact]
    public void A_backwards_clock_reads_as_Fresh_not_a_crash() =>
        Assert.Equal(NudgeMood.Fresh, NudgeMoods.For(TimeSpan.FromDays(-5)));

    [Fact]
    public void Every_mood_has_a_distinct_non_empty_line()
    {
        var lines = Enum.GetValues<NudgeMood>().Select(NudgeMoods.Line).ToList();
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }
}
