using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Shopping;

/// <summary>Two products on the shopping list that look like the SAME food — the lookalikes Eggs nudges to
/// consolidate ("we just bought two different breads"). Canonicalised so a pair has ONE identity regardless
/// of scan order: <see cref="LowerId"/> is always the smaller product id, which is also how the per-pair
/// dismissal memory keys itself, so the detector and the memory agree on what "this pair" means.</summary>
public sealed record SimilarPair(int LowerId, string LowerName, int HigherId, string HigherName);

/// <summary>
/// Finds the lookalike pairs on the shopping list. Names are compared by their core food words (trivial
/// modifiers and filler stripped, plurals folded — the ingredient matcher's rule) and their HEAD word, the
/// last core word: what the thing IS. Two names are lookalikes in exactly two shapes:
/// <list type="number">
/// <item><b>Two names for one product</b> — the shared words cover MORE than half of the shorter name
/// (<see cref="TokenContainment"/> &gt; <see cref="MinCoverage"/>) and include a head of either name:
/// "Artesano Brioche Bakery Bread" / "Brioche Style Bread Loaf" (brioche + bread, and bread heads one).</item>
/// <item><b>Two variants of one thing</b> — the shared words cover exactly half, and the two names have the
/// SAME head: "Envy Apples" / "Cosmic Crisp Apples", "Sweet Cream Salted Butter" / "Unsalted Butter"
/// (salted/unsalted are trivial). Two variants of one item are asked about (Jordan's call).</item>
/// </list>
/// <para>What is NOT a signal, and why: a shared MODIFIER — "Ground Coffee" / "Ground Beef" share "ground",
/// but the words that say what they are differ — and a head that is merely a modifier in the other name
/// ("Green Seedless Grapes" / "Grape Tomatoes"). The first version of this detector paired any two products
/// sharing a word nothing else on the list had, and a real ~120-product catalog defeated it: most words occur
/// once or twice, so every coincidence ("plain", "light", "vanilla") became a pair — 36 pairs, five real.
/// The head word is what makes a shared word evidence of the same THING rather than the same adjective.</para>
/// <para>The list-wide CLUSTER gate applies to the variant shape: a head word that heads three or more products
/// on the list (five yogurts) is a category, not a "these two specifically" signal, so "Greek Yogurt" /
/// "Vanilla Yogurt" is not nudged — per-pair that would be C(n,2) spam. A pair inside a cluster still
/// surfaces in the first shape ("Dentastix Large Breed Dog Treats" against the other Dentastix, among five dog
/// treats) — that is two names for one product, cluster or not.</para>
/// <para>⚠️ Known edge, accepted (Jordan's call): the cluster itself is NOT nudged. The fallback is manual
/// merge, which is safe now that merge is undoable. This is NOT a to-do. IF the cluster case proves annoying
/// in real use, the specific fix is a SINGLE gentle cluster heads-up ("you've got 5 yogurts — take a look"),
/// never per-pair — a separate, additive nudge kind, not a change to this detector.</para>
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

    /// <summary>A head word heading this many list products (or more) is a category head, not a pair signal.</summary>
    public const int ClusterSize = 3;

    public static IReadOnlyList<SimilarPair> Find(IReadOnlyList<Product> onList)
    {
        var words = new List<(Product Product, IReadOnlySet<string> Tokens, string Head)>(onList.Count);
        foreach (var p in onList)
        {
            // Core food words, plural-folded (the ingredient matcher's rule), with the conservative filler
            // ("style", "brand") shed the way the product matcher's fuzzy path sheds it — so "Brioche Style
            // Bread" is the same three words as "Brioche Bread".
            var tokens = IngredientMatcher.CoreTokens(p.Name)
                .Where(t => !DescriptorFilter.IsThrowaway(t))
                .Select(IngredientMatcher.Singular).ToList();
            if (tokens.Count == 0) continue; // a name with no food words can't look like anything
            words.Add((p, tokens.ToHashSet(StringComparer.Ordinal), tokens[^1]));
        }

        // How many list products each head word heads — the cluster gate's input.
        var headCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words) headCount[w.Head] = headCount.GetValueOrDefault(w.Head) + 1;

        var pairs = new List<SimilarPair>();
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = i + 1; j < words.Count; j++)
            {
                var (a, b) = (words[i], words[j]);
                var coverage = TokenContainment.Of(a.Tokens, b.Tokens);
                if (coverage < MinCoverage) continue;

                if (coverage > MinCoverage)
                {
                    // Two names for one product: most of the shorter name's words, and among them what the
                    // thing IS — a head of either name. ("Artesano Brioche Bakery Bread" / "Brioche Style
                    // Bread Loaf" share brioche + bread, and bread heads one of them.)
                    if (!b.Tokens.Contains(a.Head) && !a.Tokens.Contains(b.Head)) continue;
                }
                else
                {
                    // Exactly half: only the variant shape counts — the SAME head word on both ("Envy Apples"
                    // / "Cosmic Crisp Apples"). A head that is merely a modifier in the other name ("Green
                    // Seedless Grapes" / "Grape Tomatoes") says nothing. And inside a cluster the head is a
                    // category ("Greek Yogurt" / "Vanilla Yogurt" among five yogurts), not a pair signal.
                    if (a.Head != b.Head || headCount[a.Head] >= ClusterSize) continue;
                }

                // Canonical: lower product id first (ids are distinct, so this is unambiguous), regardless of
                // the order the two were listed — that's what gives a pair ONE identity for the memory.
                var lo = a.Product.Id < b.Product.Id ? a.Product : b.Product;
                var hi = ReferenceEquals(lo, a.Product) ? b.Product : a.Product;
                pairs.Add(new SimilarPair(lo.Id, lo.Name, hi.Id, hi.Name));
            }
        }
        return pairs;
    }
}
