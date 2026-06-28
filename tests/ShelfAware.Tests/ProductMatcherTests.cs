using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class ProductMatcherTests
{
    private static readonly IReadOnlyList<Product> Pantry =
    [
        new() { Id = 1, Name = "Pedigree Dog Food", Category = Category.PetCare },
        new() { Id = 2, Name = "Folgers Classic Coffee", Category = Category.Beverage },
        new() { Id = 3, Name = "Great Value Whole Milk", Category = Category.Dairy },
    ];

    [Theory]
    [InlineData("dog food", 1)]      // substring of a longer canonical name
    [InlineData("coffee", 2)]        // single distinctive token
    [InlineData("DOG FOOD", 1)]      // case-insensitive
    [InlineData("whole milk", 3)]    // multi-token substring
    [InlineData("Pedigree Dog Food", 1)] // exact
    public void Resolve_MatchesLooseReferences(string query, int expectedId)
    {
        var match = ProductMatcher.Resolve(query, Pantry);

        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Id);
    }

    [Theory]
    [InlineData("dish soap")]   // nothing close
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ReturnsNullWhenNothingIsCloseEnough(string query)
    {
        Assert.Null(ProductMatcher.Resolve(query, Pantry));
    }

    [Fact]
    public void Resolve_ReturnsNullForEmptyPantry()
    {
        Assert.Null(ProductMatcher.Resolve("coffee", []));
    }

    // A pantry of same-brand items: a shared store-brand prefix must not be enough to match.
    private static readonly IReadOnlyList<Product> StoreBrandPantry =
    [
        new() { Id = 1, Name = "Great Value Half & Half", Category = Category.Dairy },
        new() { Id = 2, Name = "Great Value Ultra Strong Paper Towels", Category = Category.Household },
        new() { Id = 3, Name = "Great Value Large Eggs", Category = Category.Dairy },
        new() { Id = 4, Name = "Folgers Classic Coffee", Category = Category.Beverage },
    ];

    [Theory]
    [InlineData("Great Value Broccoli Florets")]        // shares only the brand prefix with every item
    [InlineData("Great Value Disposable Paper Plates")] // shares brand + generic "paper" with the towels
    [InlineData("Great Value Whole Milk")]              // brand-only overlap, no real product
    public void Resolve_DoesNotMatchOnSharedStoreBrandPrefix(string query)
    {
        // Regression for the bug where "Great Value X" lines were merged into an unrelated "Great Value Y"
        // product because {great, value} hit the 0.5 token-overlap threshold (corrupted the price chart).
        Assert.Null(ProductMatcher.Resolve(query, StoreBrandPantry));
    }

    [Fact]
    public void Resolve_StillMatchesOnDistinctiveTokenOverlap()
    {
        // Reordered name — not exact, not a substring — must still resolve via distinctive tokens.
        var match = ProductMatcher.Resolve("Folgers Coffee Classic", StoreBrandPantry);

        Assert.NotNull(match);
        Assert.Equal(4, match!.Id);
    }
}
