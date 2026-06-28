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
    /// <summary>Best match, or null when nothing is close enough (caller should create or clarify).</summary>
    public static Product? Resolve(string? query, IReadOnlyList<Product> products)
    {
        if (string.IsNullOrWhiteSpace(query) || products.Count == 0) return null;

        var q = Normalize(query);
        if (q.Length == 0) return null;

        // 1. Exact (normalized, case-insensitive).
        var exact = products.FirstOrDefault(p => Normalize(p.Name) == q);
        if (exact is not null) return exact;

        // 2. Substring either direction ("dog food" ⊂ "pedigree dog food").
        var contains = products.FirstOrDefault(p =>
        {
            var n = Normalize(p.Name);
            return n.Contains(q) || q.Contains(n);
        });
        if (contains is not null) return contains;

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
        return bestScore >= 0.5 ? best : null;
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
