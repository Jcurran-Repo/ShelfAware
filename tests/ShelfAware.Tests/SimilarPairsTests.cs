using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class SimilarPairsTests
{
    private static Product P(int id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void Flags_two_products_that_share_a_pair_unique_food_word()
    {
        // Jordan's case: the two breads share only "brioche", which nothing else on the list owns.
        IReadOnlyList<Product> onList = [P(1, "Artesano Brioche Bread"), P(2, "Brioche Loaf"), P(3, "Whole Milk")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal(1, pair.LowerId); // canonical: smaller id first, regardless of scan order
        Assert.Equal(2, pair.HigherId);
    }

    [Fact]
    public void Does_not_flag_a_category_head_shared_by_three_or_more()
    {
        // "chicken" is in all three (a category head), so it distinguishes nothing and is NOT a signal;
        // breast/thighs/broth are each unique to one product. No lookalike pair — no spam.
        IReadOnlyList<Product> onList = [P(1, "Chicken Breast"), P(2, "Chicken Thighs"), P(3, "Chicken Broth")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Emits_a_pair_once_even_when_two_words_are_pair_unique()
    {
        // Both own "sourdough" AND "boule" (each shared by exactly these two; "fresh" is a trivial modifier
        // that's stripped) — still ONE pair, not two.
        IReadOnlyList<Product> onList = [P(1, "Sourdough Boule"), P(2, "Fresh Sourdough Boule")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Finds_nothing_when_no_food_word_is_shared_by_exactly_two()
    {
        IReadOnlyList<Product> onList = [P(1, "Whole Milk"), P(2, "Orange Juice"), P(3, "Paper Towels")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Is_aggressive_by_design_a_shared_pair_unique_word_flags_even_arguably_different_items()
    {
        // "White Bread" and "Wheat Bread" share only "bread" (unique to the two of them here) — flagged,
        // because the nudge is deliberately aggressive and per-pair dismissible: a false positive costs one
        // permanent dismiss, not a silent merge.
        IReadOnlyList<Product> onList = [P(1, "White Bread"), P(2, "Wheat Bread")];

        Assert.Single(SimilarPairs.Find(onList));
    }
}
