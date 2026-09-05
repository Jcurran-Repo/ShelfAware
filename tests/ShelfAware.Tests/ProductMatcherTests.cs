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

    [Fact]
    public void ExactMatches_returns_every_rule1_identity_not_just_the_first()
    {
        // ResolveWithKind returns the FIRST exact match and cannot say there were two — and no unique
        // index exists on product names, so "the" exact match can be a name two products share. A
        // caller about to write over it (a census attests over the stored count) needs the full set.
        // The twins here differ only in punctuation, which rule 1's own normalization folds away — the
        // pair a raw-string comparison would call distinct.
        var hyphenated = new Product { Id = 1, Name = "Home-Canned Tomato Sauce" };
        var spaced = new Product { Id = 2, Name = "Home Canned Tomato Sauce" };
        var other = new Product { Id = 3, Name = "Ketchup" };

        var twins = ProductMatcher.ExactMatches("home canned tomato sauce", [hyphenated, spaced, other]);

        Assert.Equal(2, twins.Count);
        Assert.DoesNotContain(other, twins);
        Assert.Single(ProductMatcher.ExactMatches("ketchup", [hyphenated, spaced, other]));
        Assert.Empty(ProductMatcher.ExactMatches("mustard", [hyphenated, spaced, other]));
        Assert.Empty(ProductMatcher.ExactMatches("   ", [hyphenated, spaced, other]));
    }

    [Fact]
    public void IdentityKey_folds_any_run_of_separators_to_one_space_and_is_idempotent()
    {
        // ⚠️ Normalize collapses EVERY run of separators to a single space, not just doubles. A name like
        // "Yogurt - Strawberry" has a space-hyphen-space RUN, so a single Replace("  ", " ") left
        // "yogurt  strawberry" — neither equal to the plain-spaced form nor idempotent, which a documented
        // dictionary KEY (IdentityKey, keyed on across the census's whole-pass resolution) has to be. It
        // failed safe (rule 1 missed, rule 3 caught it as similarity), but a near-key is not a key.
        Assert.Equal(
            ProductMatcher.IdentityKey("Yogurt Strawberry"),
            ProductMatcher.IdentityKey("Yogurt - Strawberry"));

        var key = ProductMatcher.IdentityKey("Yogurt - Strawberry");
        Assert.Equal("yogurt strawberry", key);
        Assert.Equal(key, ProductMatcher.IdentityKey(key)); // re-normalizing a key is a no-op
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

    // ---- which rule fired, for callers that must tell an identity from a guess ----

    [Fact]
    public void ResolveWithKind_ReportsExactAcrossPunctuationAndCase()
    {
        // ⚠️ The case the census got wrong by re-deriving exactness itself: Normalize folds punctuation
        // to spaces BEFORE rule 1, so these are the same name to the matcher — and a caller comparing
        // the raw strings would call an identity a guess, then warn the user about a guess that never
        // happened.
        IReadOnlyList<Product> pantry = [new() { Id = 9, Name = "Home-Canned Tomato Sauce", Category = Category.Pantry }];

        var (product, kind) = ProductMatcher.ResolveWithKind("home canned tomato sauce", pantry);

        Assert.Equal(9, product!.Id);
        Assert.Equal(ProductMatcher.MatchKind.ExactName, kind);
    }

    [Fact]
    public void ResolveWithKind_SeparatesSimilarityFromIdentity()
    {
        // The complement: the rules that are genuinely guesses must NOT report ExactName, or the
        // distinction buys nothing.
        Assert.Equal(ProductMatcher.MatchKind.Substring,
            ProductMatcher.ResolveWithKind("dog food", Pantry).Kind);
        Assert.Equal(ProductMatcher.MatchKind.TokenOverlap,
            ProductMatcher.ResolveWithKind("Folgers Coffee Classic", StoreBrandPantry).Kind);
        Assert.Equal(ProductMatcher.MatchKind.None,
            ProductMatcher.ResolveWithKind("wholly unrelated widget", Pantry).Kind);
    }

    [Fact]
    public void IdentityKey_of_null_is_empty()
    {
        // The `?? ""` fallback: a null name keys to empty, never a sentinel, so it can't collide with
        // a real product's identity key.
        Assert.Equal("", ProductMatcher.IdentityKey(null));
    }

    // ---- shedding pure filler so a filler-only difference ADVISES (near-miss), never blocks (DescriptorFilter) ----

    [Fact]
    public void A_filler_only_difference_reads_as_a_near_miss_to_advise_on()
    {
        // "Greek Style Yogurt" vs "Greek Yogurt" differ only by the manner word "style". Shedding it from
        // the fuzzy tokens makes them RELIABLY overlap, so the add/census surfaces advise ("use existing or
        // add anyway") — a TokenOverlap similarity, NOT an ExactName the guard would block outright. Without
        // the shed the lone "style" drags the score below 0.5 and this splits into a new product instead.
        IReadOnlyList<Product> pantry = [new() { Id = 1, Name = "Greek Yogurt", Category = Category.Dairy }];

        var (product, kind) = ProductMatcher.ResolveWithKind("Greek Style Yogurt", pantry);

        Assert.Equal(1, product!.Id);
        Assert.Equal(ProductMatcher.MatchKind.TokenOverlap, kind);
    }

    [Fact]
    public void IdentityKey_does_NOT_shed_filler_so_a_filler_name_is_never_a_hard_duplicate()
    {
        // The shed feeds the ADVISORY fuzzy path only — identity is untouched, so the duplicate guard never
        // BLOCKS a filler-only add outright and the census/rename never auto-merge it. A wrong strip is thus
        // recoverable ("add anyway"), not a silent block — the item-41 safety choice.
        Assert.NotEqual(ProductMatcher.IdentityKey("Greek Yogurt"), ProductMatcher.IdentityKey("Greek Style Yogurt"));
        Assert.Empty(ProductMatcher.ExactMatches("Greek Yogurt", [new Product { Id = 1, Name = "Greek Style Yogurt" }]));
    }

    [Fact]
    public void DescriptorFilter_sheds_only_pure_filler_never_a_distinguishing_word()
    {
        // The conservatism guard, pinned directly on the list: a distinguishing word is NEVER filler, or the
        // shed would advise a false match (whole milk vs milk). Adding any of these fails here — a deliberate,
        // reviewed gate on the membership.
        Assert.True(DescriptorFilter.IsThrowaway("style"));
        Assert.True(DescriptorFilter.IsThrowaway("brand"));
        Assert.False(DescriptorFilter.IsThrowaway("whole"));    // 2% is a different milk
        Assert.False(DescriptorFilter.IsThrowaway("classic"));  // "Folgers Classic Coffee" — a real name-word
        Assert.False(DescriptorFilter.IsThrowaway("frozen"));   // frozen vs fresh
    }

    [Fact]
    public void Resolve_matches_at_exactly_the_half_weight_threshold_keeping_the_first()
    {
        // Two products each sharing exactly one of the query's two equally-rare tokens: the overlap is
        // half the query weight, so the score lands EXACTLY on the 0.5 boundary. The threshold is
        // inclusive (>= 0.5), so it must MATCH rather than reject; and on the resulting tie the
        // FIRST-listed product must win (the scan keeps the earlier best on an equal score).
        //   With this catalog of two products: idf(alpha)=log(3/2.5), idf(bravo)=idf(charlie)=log(3/1.5),
        //   so for "Alpha Bravo" the score is w(bravo) / (w(bravo)+w(charlie)) = 0.5 exactly.
        IReadOnlyList<Product> pantry =
        [
            new() { Id = 1, Name = "Alpha Bravo", Category = Category.Pantry },
            new() { Id = 2, Name = "Alpha Charlie", Category = Category.Pantry },
        ];

        var (product, kind) = ProductMatcher.ResolveWithKind("bravo charlie", pantry);

        Assert.Equal(ProductMatcher.MatchKind.TokenOverlap, kind);
        Assert.Equal(1, product!.Id);
    }

    [Fact]
    public void Resolve_uses_the_LARGER_side_as_the_denominator_so_a_diluted_overlap_stays_below_threshold()
    {
        // A reordered query (no substring) that shares two common tokens with a product but adds a third
        // rare one: the shared weight is just under half the query weight, so it must NOT match. This
        // pins that the denominator is the SUM of the LARGER side (max(qWeight, pWeight)) — shrinking
        // either side (Sum→Max on q or p) or taking the smaller side (Max→Min) would inflate the score
        // over the line and wrongly merge these.
        IReadOnlyList<Product> pantry =
        [
            new() { Id = 1, Name = "Alpha Bravo", Category = Category.Pantry },
            new() { Id = 2, Name = "Alpha Bravo Delta", Category = Category.Pantry },
            new() { Id = 3, Name = "Charlie Echo Foxtrot Golf Hotel", Category = Category.Pantry },
        ];

        Assert.Equal(ProductMatcher.MatchKind.None,
            ProductMatcher.ResolveWithKind("alpha charlie bravo", pantry).Kind);
    }

    [Fact]
    public void Resolve_counts_an_absent_query_token_at_full_weight_so_it_can_never_be_matched()
    {
        // "sardines zzabsent": one distinctive token the catalog has, one token no product contains. The
        // absent token weighs the MAXIMUM idf (BuildIdf's floor, MaxIdf) so it fully counts against the
        // denominator and drags the score below 0.5 — an absent word can never be "matched away". This
        // pins both the idf denominator (df + 0.5) and MaxIdf's exact value: making either smaller would
        // let the absent token weigh too little and cross the threshold into a false match.
        IReadOnlyList<Product> pantry =
        [
            new() { Id = 1, Name = "Sardines Kale", Category = Category.Pantry },
            new() { Id = 2, Name = "Quinoa Lentils", Category = Category.Pantry },
        ];

        Assert.Equal(ProductMatcher.MatchKind.None,
            ProductMatcher.ResolveWithKind("sardines zzabsent", pantry).Kind);
    }

    [Fact]
    public void A_punctuation_only_product_name_never_matches_anything()
    {
        // A name with no letters or digits normalizes to "", and "" is a substring of EVERY string —
        // so one junk-named product used to win rule 2 for any query at all, ahead of the token rule,
        // and every unmatched resolve in the household came back pointing at it as a "Substring" hit.
        var junk = new Product { Id = 1, Name = "!!" };
        var real = new Product { Id = 2, Name = "Sardines Kale" };

        // Alone it must be a clean miss, not a universal substring hit.
        Assert.Equal(ProductMatcher.MatchKind.None,
            ProductMatcher.ResolveWithKind("peanut butter", [junk]).Kind);

        // Beside a real product — junk listed FIRST, the order FirstOrDefault returned it in — the
        // real one must still win by its own rule.
        var (product, kind) = ProductMatcher.ResolveWithKind("sardines kale greens", [junk, real]);
        Assert.Equal(2, product!.Id);
        Assert.Equal(ProductMatcher.MatchKind.Substring, kind);
    }
}
