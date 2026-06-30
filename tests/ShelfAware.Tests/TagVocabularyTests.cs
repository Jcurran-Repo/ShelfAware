using ShelfAware.Core.Tagging;

namespace ShelfAware.Tests;

public class TagVocabularyTests
{
    private static readonly string[] Existing = ["Condiment", "Canned", "Paper Goods"];

    [Theory]
    [InlineData("condiment")]      // casing
    [InlineData("Condiments")]     // plural
    [InlineData("  Condiment  ")]  // whitespace
    [InlineData("Sondiment")]      // one-edit typo (single substitution)
    public void FindNearDuplicate_CatchesTrivialVariants(string candidate)
    {
        Assert.Equal("Condiment", TagVocabulary.FindNearDuplicate(candidate, Existing));
    }

    [Theory]
    [InlineData("Snack")]
    [InlineData("Soft Drink")]   // a real synonym of nothing here — plain code can't know; that's the LLM's job
    [InlineData("Spice")]
    public void FindNearDuplicate_ReturnsNull_ForGenuinelyNewTags(string candidate)
    {
        Assert.Null(TagVocabulary.FindNearDuplicate(candidate, Existing));
    }
}
