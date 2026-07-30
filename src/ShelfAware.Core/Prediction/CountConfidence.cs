namespace ShelfAware.Core.Prediction;

/// <summary>
/// How much the app still believes a counted number (DESIGN.md §13.5) — one enum, because "why did we stop
/// trusting it" and "how much do we trust it" are the same fact seen twice, and storing both invites them
/// to disagree.
/// <para><b>It governs how the number is STATED, not what the number is.</b> There is one stored truth —
/// the count and the date a human vouched for it — and this decides whether a surface may assert it
/// ("4 on hand") or must attribute it ("you counted 14 on Apr 10"). That distinction is what lets §13.9's
/// rejection of coarse depth levels stand: a band here is not a second truth about the pantry, it is an
/// honest rendering of the first one's reliability.</para>
/// <para>⚠️ <b>A low-confidence count cannot be banded by DEPTH</b> — "plenty" vs "nearly out" needs a
/// consumption rate, and the whole reason confidence decayed in the <see cref="Aging"/> case is that there
/// isn't one. Elapsed time says nothing about how much got eaten. So the honest low-confidence rendering
/// attributes the number to its date rather than guessing a smaller one; only <see cref="Spent"/>, which
/// by definition HAS a rhythm, can add a depth claim.</para>
/// </summary>
public enum CountConfidence
{
    /// <summary>There is no count to believe — the product isn't counted, has never been attested, or the
    /// caller didn't pass <c>honorQuantity</c> and so didn't ask. <b>The default deliberately means
    /// "not applicable" rather than "believed"</b>: with <c>Counted</c> at zero, every uncounted product in
    /// the catalog reported that its nonexistent number was trustworthy, which is precisely the kind of
    /// implicit answer a future surface reads without checking <c>TrackQuantity</c> first.</summary>
    NotCounted = 0,

    /// <summary>Believed: attested recently, or maintained since by receipts and cooking. A surface may
    /// state the number plainly, and it decides both buy-suppression and recipe stock.</summary>
    Counted,

    /// <summary>Believed no longer, on AGE alone: the item has no learned rhythm — 0 or 1 purchases, the
    /// shape of stock bought before the app, elsewhere, gifted, or in one bulk run (§13.8) — and nobody has
    /// vouched for the number in a long time. No projection exists to say how much is left, so the number
    /// must be attributed to its date and not restated as current.</summary>
    Aging,

    /// <summary>Believed no longer, and the rhythm says why: the count is past
    /// <c>PredictionResult.CountRunsOutOn</c>. Three counted in March on a nine-day burn is long spent.
    /// This is the one low-confidence case that CAN make a depth claim, because it has a rate.</summary>
    Spent,
}
