using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Shopping;

/// <summary>Two products on the shopping list that look like the SAME food — the lookalikes Eggs nudges to
/// consolidate ("we just bought two different breads"). Canonicalised so a pair has ONE identity regardless
/// of scan order: <see cref="LowerId"/> is always the smaller product id, which is also how the per-pair
/// dismissal memory keys itself, so the detector and the memory agree on what "this pair" means.</summary>
public sealed record SimilarPair(int LowerId, string LowerName, int HigherId, string HigherName);

/// <summary>One product inside a <see cref="SimilarCluster"/>.</summary>
public sealed record ClusterMember(int Id, string Name);

/// <summary>Three or more products on the shopping list whose names all END in the same food word — five
/// "… Dog Treats" — two of which read as one product under different names. Not a pair signal (a head
/// shared by many is a category), but worth ONE gentle heads-up naming them all. <see cref="Head"/> is the shared
/// head word in its singular, folded form ("treat", "yogurt") — the identity the per-cluster dismissal memory
/// keys on, so the detector and the memory agree on what "this cluster" means. Members are in scan order.</summary>
public sealed record SimilarCluster(string Head, IReadOnlyList<ClusterMember> Members);

/// <summary>Everything one scan of the list turned up: the lookalike pairs OUTSIDE any cluster, and the
/// clusters. A product is in at most one cluster (it has one head), and a pair whose two products sit in the
/// same cluster is never emitted — the cluster's card speaks for it.</summary>
public sealed record LookalikeScan(IReadOnlyList<SimilarPair> Pairs, IReadOnlyList<SimilarCluster> Clusters);

/// <summary>
/// Finds the lookalikes on the shopping list. Names are compared by their core food words (trivial
/// modifiers and filler stripped, plurals folded — the ingredient matcher's rule) and their HEAD word, the
/// last core word: what the thing IS. Two names are a lookalike PAIR in exactly two shapes:
/// <list type="number">
/// <item><b>Two names for one product</b> — the shared words cover MORE than half of the shorter name
/// (<see cref="TokenContainment"/> &gt; <see cref="MinCoverage"/>) and include a head of either name:
/// "Artesano Brioche Bakery Bread" / "Brioche Style Bread Loaf" (brioche + bread, and bread heads one).</item>
/// <item><b>Two variants of one thing</b> — the shared words cover exactly half, OR one name is a single
/// word, and the two names have the SAME head: "Envy Apples" / "Cosmic Crisp Apples", "Milk" / "Whole Milk",
/// "Sweet Cream Salted Butter" / "Unsalted Butter" (salted/unsalted are trivial, so the second is the one
/// word "butter"). Two variants of one item are asked about (Jordan's call). A one-word name is ALWAYS
/// judged here: it is wholly contained in anything that mentions it, so its coverage is 1.0 against every
/// such name and says nothing — only a shared head does. Without this, "Grapes" / "Grape Tomatoes" paired
/// while "Green Seedless Grapes" / "Grape Tomatoes" didn't.</item>
/// </list>
/// <para>What is NOT a signal, and why: a shared MODIFIER — "Ground Coffee" / "Ground Beef" share "ground",
/// but the words that say what they are differ — and a head that is merely a modifier in the other name
/// ("Green Seedless Grapes" / "Grape Tomatoes"). The first version of this detector paired any two products
/// sharing a word nothing else on the list had, and a real ~120-product catalog defeated it: most words occur
/// once or twice, so every coincidence ("plain", "light", "vanilla") became a pair — 36 pairs, five real.
/// The head word is what makes a shared word evidence of the same THING rather than the same adjective.</para>
/// <para><b>Clusters get exactly ONE card, and only when they hold a near-twin</b> (Jordan's calls,
/// 2026-09-06/07). A head word that heads <see cref="ClusterSize"/> or more list products (five yogurts,
/// five dog treats, three steak cuts) is a category, not a "these two specifically" signal, so NO pair
/// between two of its members is ever emitted, in EITHER shape — per-pair would be C(n,2) spam. The group is
/// reported as a <see cref="SimilarCluster"/> — one gentle heads-up naming all its members — ONLY when two
/// of them are two names for one product on their own (the first shape, or equal core sets): the two
/// Dentastix among five dog treats put the whole dog-treats group on one card; three steak cuts sharing
/// nothing but "steak" stay silent, as do five flavoured yogurts (each pair exactly half). Measured on the
/// family box: every 3+ group would have been 8 cards, 7 of them categories; the twin bar leaves one. A pair
/// that straddles two groups, or sits outside any, is an ordinary pair.</para>
/// <para>⚠️ A head word is a WORD, not a concept: "Brioche Loaf" and "Brioche Bread" share no head, because
/// the detector cannot know loaf = bread. Deliberately — a synonym list is exactly the kind of guess that
/// produced the coincidence pairs above. Still deliberately looser than <see cref="IngredientMatcher"/>'s
/// strict same-food rule (mutual coverage), and a false positive costs one permanent dismiss, which is the
/// caller's to apply — this detector holds no state.</para>
/// </summary>
public static class SimilarPairs
{
    /// <summary>The shared core words must cover at least this much of the shorter name.</summary>
    public const double MinCoverage = 0.5;

    /// <summary>A head word heading this many list products (or more) makes them a <see cref="SimilarCluster"/>.</summary>
    public const int ClusterSize = 3;

    /// <summary>The most pairs one scan returns. The first shape has no cluster gate for pairs that straddle
    /// clusters, so a catalog of near-identical names with DIFFERENT heads could still be many pairs — and
    /// each new pair is a memory row the nudge service inserts on the list load. Fifty is far more than the
    /// page shows (three, plus an "…and N more" note); the ceiling only bites on a catalog that is itself the
    /// problem, and stops it from taking the process down with it. Scan order is the caller's list order, so
    /// which pairs make the cut is stable while the list doesn't change; a new product can shift the cut,
    /// which loses nothing — a hidden pair keeps its memory row and first-seen date and comes back with its
    /// mood intact. (Clusters need no cap: a product is in at most one, so there are at most n / 3.)</summary>
    public const int MaxPairs = 50;

    /// <summary>The lookalike pairs on the list — <see cref="Scan"/>'s pairs, for callers that only want those.</summary>
    public static IReadOnlyList<SimilarPair> Find(IReadOnlyList<Product> onList) => Scan(onList).Pairs;

    /// <summary>The head word of a product name — its last core food word, singular and folded, the identity a
    /// <see cref="SimilarCluster"/> keys on ("Artesano Brioche Bakery Bread" → "bread", "Dog Treats" →
    /// "treat"). Null for a name with no food words. THE one place that rule lives, so a product page asking
    /// "which cluster would this product be in?" answers exactly as the scan does.</summary>
    public static string? HeadOf(string? name) => Words(name)?.Head;

    public static LookalikeScan Scan(IReadOnlyList<Product> onList)
    {
        var words = new List<(Product Product, IReadOnlySet<string> Tokens, string Head)>(onList.Count);
        foreach (var p in onList)
        {
            if (Words(p.Name) is not { } w) continue; // a name with no food words can't look like anything
            words.Add((p, w.Tokens, w.Head));
        }

        // How many list products each head word heads — a cluster is a head with ClusterSize or more.
        var headCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words) headCount[w.Head] = headCount.GetValueOrDefault(w.Head) + 1;

        // A cluster is REPORTED only when it holds a near-twin: two members that would be "two names for one
        // product" on their own. Three steak cuts share a head and nothing else — a category, silent.
        var clusters = words.GroupBy(w => w.Head, StringComparer.Ordinal)
            .Where(g => g.Count() >= ClusterSize && HoldsATwin([.. g]))
            .Select(g => new SimilarCluster(g.Key, [.. g.Select(w => new ClusterMember(w.Product.Id, w.Product.Name))]))
            .ToList();

        var pairs = new List<SimilarPair>();
        // Stryker disable once Equality: equivalent — at i == words.Count the inner loop has no j, so words[i]
        // is never read and no pair is produced; `<=` cannot change the result. The category also suppresses
        // `>=` (the loop never runs), which IS killable — every pairing test would fail.
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = i + 1; j < words.Count; j++)
            {
                var (a, b) = (words[i], words[j]);
                // Both in the same cluster: the cluster's card speaks for them, whatever shape they'd make.
                if (a.Head == b.Head && headCount[a.Head] >= ClusterSize) continue;

                var coverage = TokenContainment.Of(a.Tokens, b.Tokens);
                if (coverage < MinCoverage) continue;

                // A one-word name ("Milk", "Grapes") is wholly contained in anything that mentions it, so
                // its coverage is 1.0 against every such name and says nothing — only a shared head can.
                var oneWord = a.Tokens.Count == 1 || b.Tokens.Count == 1;
                if (coverage > MinCoverage && !oneWord)
                {
                    // Two names for one product: most of the shorter name's words, and among them what the
                    // thing IS — a head of either name. ("Artesano Brioche Bakery Bread" / "Brioche Style
                    // Bread Loaf" share brioche + bread, and bread heads one of them.)
                    if (!b.Tokens.Contains(a.Head) && !a.Tokens.Contains(b.Head)) continue;
                }
                else
                {
                    // The variant shape — the SAME head word on both ("Envy Apples" / "Cosmic Crisp Apples",
                    // "Milk" / "Whole Milk"). A head that is merely a modifier in the other name ("Green
                    // Seedless Grapes" / "Grape Tomatoes", "Grapes" / "Grape Tomatoes") says nothing.
                    if (a.Head != b.Head) continue;
                }

                // Canonical: lower product id first (ids are distinct, so this is unambiguous), regardless of
                // the order the two were listed — that's what gives a pair ONE identity for the memory.
                // Stryker disable once Equality: equivalent — product ids are distinct, so `<` and `<=` never differ. The
                // category also suppresses `>=` (canonical order flips), which IS killable — the canonical-order test fails.
                var lo = a.Product.Id < b.Product.Id ? a.Product : b.Product;
                var hi = ReferenceEquals(lo, a.Product) ? b.Product : a.Product;
                pairs.Add(new SimilarPair(lo.Id, lo.Name, hi.Id, hi.Name));
                if (pairs.Count == MaxPairs) return new LookalikeScan(pairs, clusters);
            }
        }
        return new LookalikeScan(pairs, clusters);
    }

    // Two members of one head-word group that are two names for one product — the first pair shape, which
    // inside a group (heads already equal) reduces to: most of the shorter name's words shared, and neither
    // a one-word name (wholly contained in everything, so no evidence) — OR the two core sets are EQUAL,
    // one-word included ("Salted Butter" / "Unsalted Butter" are both {butter}: the strongest evidence
    // there is, and Jordan's variant case). "Dentastix Large Breed Dog Treats" beside "Dentastix Bacon Flavor
    // Large Breed Dog Treats" — yes; "Greek Yogurt" beside "Vanilla Yogurt" (exactly half) — no. Stops at
    // the first twin found.
    private static bool HoldsATwin(IReadOnlyList<(Product Product, IReadOnlySet<string> Tokens, string Head)> group)
    {
        // Stryker disable once Equality: equivalent — at i == group.Count the inner loop has no j, so group[i]
        // is never read; `<=` cannot change the result. The category also suppresses `>=` (the loop never
        // runs, so no cluster is ever reported), which IS killable — every reported-cluster test would fail.
        for (var i = 0; i < group.Count; i++)
        {
            for (var j = i + 1; j < group.Count; j++)
            {
                var (a, b) = (group[i].Tokens, group[j].Tokens);
                if (a.SetEquals(b)) return true;
                if (a.Count > 1 && b.Count > 1 && TokenContainment.Of(a, b) > MinCoverage) return true;
            }
        }
        return false;
    }

    // Core food words, plural-folded (the ingredient matcher's rule), with the conservative filler ("style",
    // "brand") shed the way the product matcher's fuzzy path sheds it — so "Brioche Style Bread" is the same
    // three words as "Brioche Bread" — and the head: the last of them.
    private static (IReadOnlySet<string> Tokens, string Head)? Words(string? name)
    {
        var tokens = IngredientMatcher.CoreTokens(name)
            .Where(t => !DescriptorFilter.IsThrowaway(t))
            .Select(IngredientMatcher.Singular).ToList();
        if (tokens.Count == 0) return null;
        return (tokens.ToHashSet(StringComparer.Ordinal), tokens[^1]);
    }
}
