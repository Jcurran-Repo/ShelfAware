using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Chat;

/// <summary>
/// Deterministic fuzzy resolver for chat tool calls (DESIGN.md §7): maps a loose product
/// reference like "dog food" to the canonical product "Pedigree Dog Food". The model also
/// fuzzy-matches against the product list it's given; this is the C#-side safety net that
/// turns the name it returns into a concrete product, and the unit-testable seam for it.
/// </summary>
public static class ProductMatcher
{
    /// <summary>Which rule produced a match. Callers that merely need "the product" ignore this; callers
    /// deciding how much to TRUST the match need it, because the three rules are not equally strong —
    /// rule 1 is an identity, rules 2 and 3 are similarity.
    /// <para>⚠️ It exists so a caller cannot re-derive exactness by comparing raw strings. That looks
    /// equivalent and isn't: <see cref="Normalize"/> folds punctuation to spaces, so a raw comparison
    /// calls "Home Canned Tomato Sauce" a fuzzy hit on "Home-Canned Tomato Sauce" when rule 1 matched
    /// them outright — and the surface then warns about a guess that never happened.</para></summary>
    public enum MatchKind
    {
        /// <summary>Nothing close enough; the product is null.</summary>
        None,
        /// <summary>Rule 1 — the same name once punctuation and case are folded. An identity, not a guess.</summary>
        ExactName,
        /// <summary>Rule 2 — one name contains the other. Similarity.</summary>
        Substring,
        /// <summary>Rule 3 — enough distinctive token weight overlaps. Similarity.</summary>
        TokenOverlap,
    }

    /// <summary>Best match, or null when nothing is close enough (caller should create or clarify).</summary>
    public static Product? Resolve(string? query, IReadOnlyList<Product> products) =>
        ResolveWithKind(query, products).Product;

    /// <summary>Every product rule 1 calls an identity for this query — same normalization, full set.
    /// <see cref="ResolveWithKind"/> returns the FIRST and cannot say there were two, and no unique
    /// index exists on product names — so a caller about to write over "the" exact match needs to know
    /// when that name is actually a name two products share (§13.8's twins rule: a census attests over
    /// a product's stored count, and picking a twin arbitrarily replaces the wrong household number).</summary>
    public static IReadOnlyList<Product> ExactMatches(string? query, IReadOnlyList<Product> products)
    {
        if (string.IsNullOrWhiteSpace(query) || products.Count == 0) return [];
        var q = Normalize(query);
        if (q.Length == 0) return [];
        return [.. products.Where(p => Normalize(p.Name) == q)];
    }

    /// <summary>As <see cref="Resolve"/>, and says which rule fired — for callers that must tell an
    /// identity from a similarity (a census attests over a product's stored count, so it may not
    /// pre-authorize a guess at WHICH product).</summary>
    public static (Product? Product, MatchKind Kind) ResolveWithKind(string? query, IReadOnlyList<Product> products)
    {
        if (string.IsNullOrWhiteSpace(query) || products.Count == 0) return (null, MatchKind.None);

        var q = Normalize(query);
        if (q.Length == 0) return (null, MatchKind.None);

        // 1. Exact (normalized, case-insensitive).
        var exact = products.FirstOrDefault(p => Normalize(p.Name) == q);
        if (exact is not null) return (exact, MatchKind.ExactName);

        // 2. Substring either direction ("dog food" ⊂ "pedigree dog food").
        var contains = products.FirstOrDefault(p =>
        {
            var n = Normalize(p.Name);
            return n.Contains(q) || q.Contains(n);
        });
        if (contains is not null) return (contains, MatchKind.Substring);

        // 3. Weighted token-overlap. Weight each token by how rare it is across the catalog (IDF) so a
        //    shared store-brand prefix ("Great Value") or generic word ("paper") can't drive a match on
        //    its own — only distinctive tokens (broccoli, towels) carry real weight. Without this, two
        //    unrelated "Great Value X"/"Great Value Y" items overlap on {great, value} and score the bare
        //    0.5 threshold, merging e.g. Broccoli Florets into Half & Half.
        var qTokens = Tokens(q);
        var idf = BuildIdf(products);
        double Weight(string t) => idf.TryGetValue(t, out var w) ? w : MaxIdf(products.Count);
        var qWeight = qTokens.Sum(Weight);

        Product? best = null;
        var bestScore = 0.0;
        foreach (var p in products)
        {
            var pTokens = Tokens(Normalize(p.Name));
            if (pTokens.Count == 0) continue;
            var sharedWeight = qTokens.Where(pTokens.Contains).Sum(Weight);
            var pWeight = pTokens.Sum(Weight);
            var score = sharedWeight / Math.Max(qWeight, pWeight);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        // A solid majority of the distinctive token weight must overlap.
        return bestScore >= 0.5 ? (best, MatchKind.TokenOverlap) : (null, MatchKind.None);
    }

    /// <summary>
    /// Smoothed inverse document frequency per token over the product catalog: a token in every product
    /// scores ~0, a token in a single product scores high. Lets <see cref="Resolve"/> ignore boilerplate
    /// brand/qualifier words without hard-coding a brand list.
    /// </summary>
    private static Dictionary<string, double> BuildIdf(IReadOnlyList<Product> products)
    {
        var df = new Dictionary<string, int>();
        foreach (var p in products)
            foreach (var t in Tokens(Normalize(p.Name)))
                df[t] = df.GetValueOrDefault(t) + 1;
        return df.ToDictionary(kv => kv.Key, kv => Math.Log((products.Count + 1.0) / (kv.Value + 0.5)));
    }

    // Weight for a query token that appears in no product (maximally distinctive, so it counts fully
    // against the denominator and can never be "matched").
    private static double MaxIdf(int productCount) => Math.Log((productCount + 1.0) / 0.5);

    private static HashSet<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Trim()
            .Replace("  ", " ");
}
