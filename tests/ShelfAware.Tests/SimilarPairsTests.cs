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
    public void Inside_a_cluster_even_a_twin_pair_folds_into_the_cluster_card()
    {
        // Five dog treats make "treats" a cluster. The two Dentastix share 5 of 5 core words, but a cluster
        // gets exactly ONE card (Jordan's call): they're on the dog-treats card, not on a card of their own.
        IReadOnlyList<Product> onList =
        [
            P(1, "Dentastix Bacon Flavor Large Breed Dog Treats"), P(2, "Dentastix Large Breed Dog Treats"),
            P(3, "Chicken Jerky Dog Treats"), P(4, "Rawhide Sticks Dog Treats"), P(5, "Duck Wrapped Cod Dog Treats"),
        ];

        var scan = SimilarPairs.Scan(onList);

        Assert.Empty(scan.Pairs);
        var cluster = Assert.Single(scan.Clusters);
        Assert.Equal("treat", cluster.Head);
        Assert.Equal([1, 2, 3, 4, 5], cluster.Members.Select(m => m.Id));
    }

    [Fact]
    public void A_pair_outside_a_cluster_is_still_a_pair()
    {
        // The brioche twins are two breads among just two breads (no bread cluster), beside a reported
        // treats cluster — the cluster fold only removes pairs whose BOTH members share the cluster's head.
        IReadOnlyList<Product> onList =
        [
            P(1, "Artesano Brioche Bakery Bread"), P(2, "Brioche Style Bread Loaf"),
            P(3, "Dentastix Large Breed Dog Treats"), P(4, "Dentastix Bacon Large Breed Dog Treats"), P(5, "Duck Wrapped Cod Dog Treats"),
        ];

        var scan = SimilarPairs.Scan(onList);

        var pair = Assert.Single(scan.Pairs);
        Assert.Equal((1, 2), (pair.LowerId, pair.HigherId));
        Assert.Equal("treat", Assert.Single(scan.Clusters).Head);
    }

    [Fact]
    public void A_cluster_is_reported_only_when_two_members_are_two_names_for_one_product()
    {
        // Three sauces, two of them the punctuation twins "Home Canned" / "Home-Canned" (equal core sets) —
        // a reported cluster. Three steak cuts sharing nothing but "steak" — a silent category: no cluster,
        // and (the gate the cluster always applied) no pairs between them either.
        IReadOnlyList<Product> sauces = [P(1, "Home Canned Tomato Sauce"), P(2, "Home-Canned Tomato Sauce"), P(3, "Sriracha Sauce")];
        IReadOnlyList<Product> steaks = [P(1, "Chuck Eye Steak"), P(2, "Coulotte Steak"), P(3, "NY Strip Steak")];

        Assert.Equal("sauce", Assert.Single(SimilarPairs.Scan(sauces).Clusters).Head);
        var silent = SimilarPairs.Scan(steaks);
        Assert.Empty(silent.Clusters);
        Assert.Empty(silent.Pairs);
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
    public void Sharing_only_the_head_of_a_longer_name_is_not_half()
    {
        // Same head ("bread"), but it is 1 of the 3 core words of each — under half. Two breads with nothing
        // else in common are two breads, not two names for one; the variant shape needs half.
        IReadOnlyList<Product> onList = [P(1, "Sliced Plain French Bread"), P(2, "Artesano Brioche Bakery Bread")];

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
    public void Three_variants_sharing_only_a_head_are_a_silent_category()
    {
        // "yogurt" heads three products, each pair exactly half — a category, not a "these two specifically"
        // signal. Per-pair would be three nudges about one shelf, and with no near-twin among them there is
        // no cluster card either: nothing.
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Strawberry Yogurts")];

        var scan = SimilarPairs.Scan(onList);

        Assert.Empty(scan.Pairs);
        Assert.Empty(scan.Clusters);
    }

    [Fact]
    public void A_reported_cluster_lists_its_members_in_scan_order_under_the_folded_head()
    {
        IReadOnlyList<Product> onList =
            [P(1, "Dentastix Large Breed Dog Treats"), P(2, "Chicken Jerky Dog Treat"), P(3, "Dentastix Bacon Large Breed Dog Treats")];

        var cluster = Assert.Single(SimilarPairs.Scan(onList).Clusters);

        Assert.Equal("treat", cluster.Head);
        Assert.Equal([1, 2, 3], cluster.Members.Select(m => m.Id));
    }

    [Fact]
    public void Two_products_sharing_a_head_are_not_a_cluster()
    {
        // Below ClusterSize there is no cluster — the two are a variant PAIR (the test right below).
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Whole Milk")];

        Assert.Empty(SimilarPairs.Scan(onList).Clusters);
    }

    [Fact]
    public void Find_is_the_scan_s_pairs()
    {
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Whole Milk")];

        Assert.Equal(SimilarPairs.Scan(onList).Pairs, SimilarPairs.Find(onList));
    }

    [Fact]
    public void HeadOf_is_the_last_core_word_singular_with_filler_shed()
    {
        Assert.Equal("bread", SimilarPairs.HeadOf("Artesano Brioche Bakery Bread"));
        Assert.Equal("treat", SimilarPairs.HeadOf("Dentastix Large Breed Dog Treats"));
        Assert.Equal("yogurt", SimilarPairs.HeadOf("Greek Yogurt Style")); // "style" is filler, not a head
        Assert.Null(SimilarPairs.HeadOf("Style Brand"));                    // nothing but filler
        Assert.Null(SimilarPairs.HeadOf(null));
    }

    [Fact]
    public void Two_products_sharing_a_head_are_a_pair_right_up_to_the_cluster_size()
    {
        // The cluster gate is ≥ 3 holders of the head; exactly two is the variant shape.
        IReadOnlyList<Product> onList = [P(1, "Greek Yogurt"), P(2, "Vanilla Yogurt"), P(3, "Whole Milk")];

        Assert.Single(SimilarPairs.Find(onList));
    }

    [Fact]
    public void A_one_word_name_pairs_only_with_a_same_head_name()
    {
        // "Milk" is wholly contained in every name that mentions milk, so containment says nothing about it;
        // it pairs with "Whole Milk" (same head) but not with "Milk Chocolate Bar" (milk is a modifier there).
        IReadOnlyList<Product> onList = [P(1, "Milk"), P(2, "Whole Milk"), P(3, "Milk Chocolate Bar")];

        var pair = Assert.Single(SimilarPairs.Find(onList));

        Assert.Equal((1, 2), (pair.LowerId, pair.HigherId));
    }

    [Fact]
    public void A_one_word_name_does_not_pair_with_a_name_that_only_uses_it_as_a_modifier()
    {
        // Same semantics as the seedless-grapes case, with the adjectives gone: still not a pair.
        IReadOnlyList<Product> onList = [P(1, "Grapes"), P(2, "Grape Tomatoes")];

        Assert.Empty(SimilarPairs.Find(onList));
    }

    [Fact]
    public void A_one_word_name_inside_a_cluster_is_silenced_with_it_and_is_no_twin_evidence()
    {
        // Four milks: "Milk" would otherwise pair with each of the other three — the per-pair spam the cluster
        // exists to stop, arriving through a one-word name. And a one-word name is contained in everything, so
        // it can't be the near-twin that earns the group a card: nothing.
        IReadOnlyList<Product> onList = [P(1, "Milk"), P(2, "Whole Milk"), P(3, "Oat Milk"), P(4, "Almond Milk")];
        // …whichever side of a comparison the one-word name falls on (it's the LAST member here).
        IReadOnlyList<Product> reversed = [P(1, "Whole Milk"), P(2, "Oat Milk"), P(3, "Almond Milk"), P(4, "Milk")];

        foreach (var list in new[] { onList, reversed })
        {
            var scan = SimilarPairs.Scan(list);

            Assert.Empty(scan.Pairs);
            Assert.Empty(scan.Clusters);
        }
    }

    [Fact]
    public void Twins_whose_core_words_are_identical_earn_their_cluster_a_card_even_when_one_word()
    {
        // "Salted Butter" and "Unsalted Butter" are both the one word {butter} — equal sets, the strongest
        // evidence there is, and Jordan's own variant case. With a "Peanut Butter" on the list that's three
        // butters: one cluster card naming all three, not a twin card plus a cluster card about the same shelf.
        // Without the third, the two are an ordinary variant pair (the butter test above).
        IReadOnlyList<Product> onList = [P(1, "Salted Butter"), P(2, "Unsalted Butter"), P(3, "Peanut Butter")];

        var scan = SimilarPairs.Scan(onList);

        Assert.Empty(scan.Pairs);
        Assert.Equal([1, 2, 3], Assert.Single(scan.Clusters).Members.Select(m => m.Id));
    }

    // ── General ──

    [Fact]
    public void Caps_the_pairs_it_returns_so_a_pathological_catalog_cannot_explode()
    {
        // Eleven "Whole Milk Jug N" (head jug) and eleven "Jug Whole Milk N" (head milk): same core words
        // (digits are dropped), so every CROSS pair is two names for one product — 121 of them — while each
        // group is a cluster of eleven whose inner pairs fold away. Every new pair becomes a memory row on the
        // list load; the ceiling holds it to MaxPairs, in scan order, so the same pairs make the cut on every
        // visit.
        IReadOnlyList<Product> onList =
        [
            .. Enumerable.Range(1, 11).Select(i => P(i, $"Whole Milk Jug {i}")),
            .. Enumerable.Range(12, 11).Select(i => P(i, $"Jug Whole Milk {i}")),
        ];

        var first = SimilarPairs.Scan(onList);
        var again = SimilarPairs.Scan(onList);

        Assert.Equal(SimilarPairs.MaxPairs, first.Pairs.Count);
        Assert.Equal(first.Pairs, again.Pairs);
        Assert.Equal((1, 12), (first.Pairs[0].LowerId, first.Pairs[0].HigherId));
        Assert.Equal(2, first.Clusters.Count); // the cap stops the pair scan, never the clusters
    }


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
        // "style" is throwaway filler (DescriptorFilter). Shed, the loaf is {brioche, bread, loaf} and shares
        // 2 of 3 with the other bread (plus the head "bread") — a pair. Kept, it is four words sharing two:
        // exactly half, with differing heads (loaf / bread) — no pair. So the shed decides this fixture.
        IReadOnlyList<Product> onList = [P(1, "Brioche Style Bread Loaf"), P(2, "Artesano Brioche Bakery Bread")];

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
