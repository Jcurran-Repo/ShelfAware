using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

public class SimilarPairsTests
{
    private static Product P(int id, string name) => new() { Id = id, Name = name };

    // ── Shape 1: two names for one product (a strict majority of the shorter name, including a head) ──

    [Fact]
    public void Flags_two_names_for_one_product_that_share_most_words_and_a_head()
    {
        // Jordan's real breads: brioche + bread shared (2 of the loaf's 3 core words — "style" is filler),
        // and "bread" is the head of one of them.
        IReadOnlyList<Product> onList =
            [P(1, "Artesano Brioche Bakery Bread"), P(2, "Brioche Style Bread Loaf"), P(3, "Whole Milk")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal(1, pair.LowerId);
        Assert.Equal(2, pair.HigherId);
    }

    [Fact]
    public void A_majority_of_shared_words_that_are_all_modifiers_is_not_a_pair()
    {
        // "all" + "purpose" are 2 of the tomatoes' 3 core words, but neither name's head (tomatoes / spray)
        // is shared — two products that happen to wear the same adjectives, not one product.
        IReadOnlyList<Product> onList = [P(1, "All Purpose Crushed Tomatoes"), P(2, "All Purpose Cleaner Spray")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Inside_a_cluster_a_pair_still_surfaces_when_it_shares_a_strict_majority()
    {
        // Five dog treats make "treats" a category head, yet the two Dentastix share 5 of 5 core words —
        // two names for one product, cluster or not.
        IReadOnlyList<Product> onList =
        [
            P(1, "Dentastix Bacon Flavor Large Breed Dog Treats"), P(2, "Dentastix Large Breed Dog Treats"),
            P(3, "Chicken Jerky Dog Treats"), P(4, "Rawhide Sticks Dog Treats"), P(5, "Duck Wrapped Cod Dog Treats"),
        ];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal((1, 2), (pair.LowerId, pair.HigherId));
    }

    // ── Shape 2: two variants of one thing (exactly half the words, the SAME head) ──

    [Fact]
    public void Flags_two_variants_that_share_their_head_word()
    {
        // Exactly half of each two-word name is shared, and it's the head ("apple", plural-folded via
        // IngredientMatcher.Singular) — two varieties of one item, which the app treats as one product.
        IReadOnlyList<Product> onList = [P(1, "Envy Apples"), P(2, "Cosmic Crisp Apple"), P(3, "Whole Milk")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal((1, 2), (pair.LowerId, pair.HigherId));
    }

    [Fact]
    public void Salted_and_unsalted_butter_are_two_variants_of_one_item()
    {
        // salted/unsalted are trivial modifiers, so "Unsalted Butter" is the one word "butter", wholly
        // contained in the other — asked about, per Jordan's call.
        IReadOnlyList<Product> onList = [P(1, "Sweet Cream Salted Butter"), P(2, "Unsalted Butter")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    [Fact]
    public void A_shared_modifier_alone_is_not_a_pair()
    {
        // "ground" is half of each name, but the heads (coffee / beef) differ: same adjective, different thing.
        IReadOnlyList<Product> onList = [P(1, "Ground Coffee"), P(2, "Ground Beef")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void A_head_that_is_only_a_modifier_in_the_other_name_is_not_a_pair()
    {
        // "grape" heads the grapes but merely modifies the tomatoes; half of "Grape Tomatoes" is shared, yet
        // the two are not variants of one thing.
        IReadOnlyList<Product> onList = [P(1, "Green Seedless Grapes"), P(2, "Grape Tomatoes")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Does_not_flag_variants_inside_a_cluster()
    {
        // "yogurt" heads three products — a category, not a "these two specifically" signal. Per-pair would be
        // three nudges about one shelf.
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Strawberry Yogurt")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Two_products_sharing_a_head_are_a_pair_right_up_to_the_cluster_size()
    {
        // The cluster gate is ≥ 3 holders of the head; exactly two is the variant shape.
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Whole Milk")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    // ── General ──

    [Fact]
    public void Canonicalises_to_the_lower_id_regardless_of_scan_order()
    {
        // The higher-id product is listed FIRST; the pair must still name the smaller id as LowerId, so the
        // pair has one identity for the dismissal/mood memory however the list happened to be ordered.
        IReadOnlyList<Product> onList = [P(5, "Brioche Bread Loaf"), P(2, "Artesano Brioche Bread")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal(2, pair.LowerId);
        Assert.Equal("Artesano Brioche Bread", pair.LowerName);
        Assert.Equal(5, pair.HigherId);
        Assert.Equal("Brioche Bread Loaf", pair.HigherName);
    }

    [Fact]
    public void Does_not_flag_a_category_head_shared_by_three_or_more_different_cuts()
    {
        // "chicken" is in all three, but as a modifier: the heads (breast / thighs / broth) all differ.
        IReadOnlyList<Product> onList = [P(1, "Chicken Breast"), P(2, "Chicken Thighs"), P(3, "Chicken Broth")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Sheds_filler_words_before_comparing()
    {
        // "style" is throwaway filler (DescriptorFilter): without shedding it "Brioche Style Bread" would be
        // three words sharing two with "Brioche Bread" — still a pair here, but "Greek Style Yogurt" against
        // "Greek Yogurt" is the sharper case: with the filler kept it's 2 of 3, with it shed it's identical.
        IReadOnlyList<Product> onList = [P(1, "Greek Style Yogurt"), P(2, "Greek Yogurt")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Emits_a_pair_once()
    {
        // The same two products are one pair however many words they share ("fresh" is trivial).
        IReadOnlyList<Product> onList = [P(1, "Sourdough Boule"), P(2, "Fresh Sourdough Boule")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    [Fact]
    public void Finds_nothing_when_nothing_overlaps()
    {
        IReadOnlyList<Product> onList = [P(1, "Whole Milk"), P(2, "Orange Juice"), P(3, "Paper Towels")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void A_name_with_no_food_words_pairs_with_nothing()
    {
        // "4 oz" is all units and numbers — no core words, so it can't look like anything (and must not throw).
        IReadOnlyList<Product> onList = [P(1, "4 oz"), P(2, "Whole Milk"), P(3, "Chocolate Milk")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal((2, 3), (pair.LowerId, pair.HigherId));
    }

    [Fact]
    public void Is_still_aggressive_by_design_two_types_with_one_head_are_asked_about()
    {
        // "White Bread" and "Wheat Bread" share only "bread" — but it's the head of both and nothing else on
        // the list is a bread, so they're two variants of one thing to the detector. A false positive costs
        // one permanent dismiss, not a silent merge.
        IReadOnlyList<Product> onList = [P(1, "White Bread"), P(2, "Wheat Bread")];

        Assert.Single(SimilarPairs.Find(onList));
    }
}
