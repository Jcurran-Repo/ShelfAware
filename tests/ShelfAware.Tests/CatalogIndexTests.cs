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

    [Fact]
    public void ExactMatches_returns_every_product_sharing_the_identity()
    {
        // "Half-and-Half" and "Half and Half" normalize to one identity key, so both come back — the whole
        // reason a census can't silently attest over "the" exact match.
        var index = new CatalogIndex([P(1, "Half-and-Half"), P(2, "Half and Half"), P(3, "Whole Milk")]);

        Assert.Equal([1, 2], index.ExactMatches("half and half").Select(p => p.Id).OrderBy(id => id));
        Assert.Equal([3], index.ExactMatches("whole milk").Select(p => p.Id));
        Assert.Empty(index.ExactMatches("nothing like it"));
    }

    [Fact]
    public void A_punctuation_only_name_is_never_indexed_or_matched()
    {
        // "!!" and "@@" both have an empty identity key: the ctor skips them and ExactMatches refuses an
        // empty key, so two distinct junk names can't merge under "".
        var index = new CatalogIndex([P(1, "!!"), P(2, "@@"), P(3, "Milk")]);

        Assert.Empty(index.ExactMatches("!!"));
        Assert.Empty(index.ExactMatches(""));
        Assert.Empty(index.ExactMatches(null));
        Assert.Equal(3, index.ById(3)?.Id);
        Assert.Null(index.ById(99));
    }
}
