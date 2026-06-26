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

        // 3. Token-overlap score; require a solid majority to avoid wrong matches.
        var qTokens = Tokens(q);
        Product? best = null;
        var bestScore = 0.0;
        foreach (var p in products)
        {
            var pTokens = Tokens(Normalize(p.Name));
            if (pTokens.Count == 0) continue;
            var overlap = qTokens.Count(pTokens.Contains);
            var score = (double)overlap / Math.Max(qTokens.Count, pTokens.Count);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return bestScore >= 0.5 ? best : null;
    }

    private static HashSet<string> Tokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Trim()
            .Replace("  ", " ");
}
