using ShelfAware.Core.Chat;

namespace ShelfAware.Tests;

public class TokenContainmentTests
{
    private static IReadOnlySet<string> S(params string[] t) => t.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Divides_the_overlap_by_the_smaller_set()
    {
        // Containment, not Jaccard: a short label wholly inside a long name scores 1.0, however long the name.
        Assert.Equal(1.0, TokenContainment.Of(S("lean", "ground", "beef"), S("great", "value", "lean", "ground", "beef")));
        Assert.Equal(2.0 / 3, TokenContainment.Of(S("brioche", "bread", "loaf"), S("artesano", "brioche", "bakery", "bread")), 10);
    }

    [Fact]
    public void Is_symmetric()
    {
        var (a, b) = (S("ground", "coffee"), S("ground", "beef", "chuck"));
        Assert.Equal(TokenContainment.Of(a, b), TokenContainment.Of(b, a));
        Assert.Equal(0.5, TokenContainment.Of(a, b));
    }

    [Fact]
    public void Is_zero_when_either_side_is_empty()
    {
        Assert.Equal(0, TokenContainment.Of(S(), S("milk")));
        Assert.Equal(0, TokenContainment.Of(S("milk"), S()));
    }

    [Fact]
    public void Is_zero_with_no_overlap()
    {
        Assert.Equal(0, TokenContainment.Of(S("whole", "milk"), S("orange", "juice")));
    }
}
