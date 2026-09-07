namespace ShelfAware.Core.Chat;

/// <summary>
/// The token containment coefficient: |A ∩ B| / min(|A|, |B|) — how much of the SHORTER token set the
/// two share. THE one definition of "these two names overlap enough" in the app: the extraction scorer
/// grades a found line against its label with it (a concise label like "Lean Ground Beef" against a
/// verbose read like "Great Value 93% Lean Ground Beef" scores 1.0, which symmetric Jaccard wrongly
/// penalised), and the lookalike detector asks it of two product names. Each caller brings its OWN
/// tokens — the scorer's are every word, the detector's are core food words — so what a token IS stays
/// the caller's rule; only the arithmetic lives here.
/// </summary>
public static class TokenContainment
{
    /// <summary>|A ∩ B| / min(|A|, |B|); 0 when either set is empty (nothing to contain).</summary>
    public static double Of(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / Math.Min(a.Count, b.Count);
    }
}
