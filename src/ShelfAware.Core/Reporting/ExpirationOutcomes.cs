using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Reporting;

/// <summary>What actually became of a dated purchase, as far as the data can say.</summary>
public enum LabelOutcome
{
    /// <summary>The label date hasn't passed yet ("best by" day itself is still good — v3.6's rule).</summary>
    StillAhead,
    /// <summary>The product was bought again before the label — the old one was presumably finished
    /// or rotated out. Rebuying supersedes, exactly as it does in the predictor.</summary>
    Superseded,
    /// <summary>An OutNow was signaled before the label — it was finished, not lost.</summary>
    MarkedOut,
    /// <summary>Restocked AFTER the label — the v3.6 "I froze it" override; the human said it's fine.</summary>
    Overridden,
    /// <summary>The label passed and nothing in the data says it was finished, replaced, or saved.
    /// NOT proof of waste — a jug finished quietly leaves no trace — but the honest "worth checking"
    /// bucket, and the only claim Waste Watch ever makes.</summary>
    PassedQuietly,
}

/// <summary>One dated purchase, flat for judging.</summary>
public sealed record LabeledPurchase(
    int ProductId, string ProductName, DateOnly PurchasedAt, DateOnly Label, decimal? Price);

/// <summary>
/// The retrospective half of the expiration feature: v3.6 derives the CURRENT expired state; this
/// judges what happened to each PAST dated purchase, using only evidence the app actually has.
/// The deliberate limit: absence of evidence is never reported as waste — Waste Watch phrases its
/// findings as "label passed, worth checking", with dollars AT STAKE, never "you wasted $X".
/// </summary>
public static class ExpirationOutcomes
{
    public static LabelOutcome Judge(
        LabeledPurchase purchase,
        IReadOnlyCollection<DateOnly> productPurchaseDates,
        IReadOnlyCollection<(DateOnly At, SignalKind Kind)> productSignals,
        DateOnly today)
    {
        if (purchase.Label >= today) return LabelOutcome.StillAhead;

        // Evidence it was consumed or replaced before the label ran out wins first.
        if (productPurchaseDates.Any(d => d > purchase.PurchasedAt && d <= purchase.Label))
            return LabelOutcome.Superseded;

        if (productSignals.Any(s => s.Kind == SignalKind.OutNow
                && s.At >= purchase.PurchasedAt && s.At <= purchase.Label))
            return LabelOutcome.MarkedOut;

        // Restocked ON/BEFORE the label is just "I have it" (a casual tap must not disarm anything,
        // v3.6's rule); only a restock AFTER the label is the human overriding the sticker.
        if (productSignals.Any(s => s.Kind == SignalKind.Restocked && s.At > purchase.Label))
            return LabelOutcome.Overridden;

        return LabelOutcome.PassedQuietly;
    }
}
