using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class CatalogIndexTests
{
    private static Product P(int id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void Repeated_and_distinct_resolves_stay_correct_through_the_memo()
    {
        // ResolveWithKind memoizes per query (the catalog is immutable for the index's lifetime, so a
        // resolve is a pure function of the name). Asking twice must give the same answer, and a second
        // QUERY must never be served the first query's entry.
        var index = new CatalogIndex([P(1, "Whole Milk"), P(2, "Ground Beef")]);

        var milkFirst = index.ResolveWithKind("Whole Milk");
        var beef = index.ResolveWithKind("Ground Beef");
        var milkAgain = index.ResolveWithKind("Whole Milk");

        Assert.Equal(1, milkFirst.Product?.Id);
        Assert.Equal(2, beef.Product?.Id); // a distinct query is not handed the earlier query's answer
        Assert.Equal(milkFirst, milkAgain);
        Assert.Equal(ProductMatcher.MatchKind.ExactName, milkAgain.Kind);
        Assert.Null(index.ResolveWithKind(null).Product); // the null/blank query stays a clean miss
    }
}
