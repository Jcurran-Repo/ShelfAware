using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Shopping;

/// <summary>Two products on the shopping list that look like the SAME food — the lookalikes Eggs nudges to
/// consolidate ("we just bought two different breads"). Canonicalised so a pair has ONE identity regardless
/// of scan order: <see cref="LowerId"/> is always the smaller product id, which is also how the per-pair
/// dismissal memory keys itself, so the detector and the memory agree on what "this pair" means.</summary>
public sealed record SimilarPair(int LowerId, string LowerName, int HigherId, string HigherName);

/// <summary>
/// Finds the lookalike pairs on the shopping list. The signal: two products share a core food word that
/// NOTHING ELSE on the list has — a word in EXACTLY those two products. "Artesano Brioche Bread" and
/// "Brioche Loaf" both own "brioche" (and nothing else does), so they surface; five chicken products all
/// share "chicken", which is then a category head (df ≥ 3), not a pair signal, so they don't spam.
/// <para>Deliberately AGGRESSIVE (Jordan's call — "the aggressive nudge catches the rest" that the
/// conservative descriptor shed won't merge), and deliberately looser than <see cref="IngredientMatcher"/>'s
/// strict same-food rule, which needs MUTUAL coverage and so misses a pair that shares only its
/// distinguishing word. A false positive (two genuinely-different foods that happen to share a pair-unique
/// word) costs one permanent dismiss, which is the caller's to apply — this detector holds no state.</para>
/// <para>⚠️ Known edge, accepted (Jordan's call): a CLUSTER — three+ products all sharing a head word (five
/// yogurts) — is NOT nudged. Per-pair that would be C(n,2) spam, and a head shared by many is a category, not
/// a "these two specifically" signal. The fallback is manual merge, which is safe now that merge is undoable.
/// This is NOT a to-do. IF the cluster case proves annoying in real use, the specific fix is a SINGLE gentle
/// cluster heads-up ("you've got 5 yogurts — take a look"), never per-pair, linking to the products filtered
/// to them where they merge manually — a separate, additive nudge kind, not a change to this detector.</para>
/// </summary>
public static class SimilarPairs
{
    public static IReadOnlyList<SimilarPair> Find(IReadOnlyList<Product> onList)
    {
        // token -> the products whose name contains it (each product at most once per token).
        var byToken = new Dictionary<string, List<Product>>(StringComparer.Ordinal);
        foreach (var p in onList)
        {
            foreach (var t in IngredientMatcher.CoreTokens(p.Name).Distinct())
            {
                if (!byToken.TryGetValue(t, out var holders)) byToken[t] = holders = [];
                holders.Add(p);
            }
        }

        var seen = new HashSet<(int, int)>();
        var pairs = new List<SimilarPair>();
        foreach (var holders in byToken.Values)
        {
            // Exactly two products own this word ⇒ it is unique to that pair. Three+ is a category head
            // (bread, chicken), which distinguishes nothing, so it is NOT a signal.
            if (holders.Count != 2) continue;
            // Canonical: lower product id first (ids are distinct, so this is unambiguous), regardless of the
            // order the two happened to be scanned — that's what gives a pair ONE identity for the memory.
            var lo = holders.MinBy(h => h.Id)!;
            var hi = holders.MaxBy(h => h.Id)!;
            // Two words might both be pair-unique to the same pair ("brioche" AND "bread"): one pair, once.
            if (seen.Add((lo.Id, hi.Id))) pairs.Add(new SimilarPair(lo.Id, lo.Name, hi.Id, hi.Name));
        }
        return pairs;
    }
}
