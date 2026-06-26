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
}
