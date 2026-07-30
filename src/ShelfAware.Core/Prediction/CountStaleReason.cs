namespace ShelfAware.Core.Prediction;

/// <summary>
/// Why a counted product's number stopped being trusted (DESIGN.md §13.5). Two genuinely different
/// findings that a surface has to word differently — and one of them is the only staleness check that
/// exists for stock the receipts never see.
/// </summary>
public enum CountStaleReason
{
    /// <summary>The count is still believed.</summary>
    None = 0,

    /// <summary>The item has a learned rhythm and the count is past the date that rhythm projected —
    /// <c>PredictionResult.CountRunsOutOn</c>, which is populated in this case. Three counted in March on
    /// a nine-day burn is long spent.</summary>
    PastItsProjection,

    /// <summary>The item has NO rhythm to project from — 0 or 1 purchases, which is the shape of stock
    /// bought before the app, bought elsewhere, gifted, or in one bulk run — and nobody has vouched for
    /// the number in a long time.
    /// <para>This is the fallback that keeps §13's promise where the rest of §13 can't reach: a count
    /// with no purchase history gets no automated <c>+</c> from receipts and no exhaustion date, so
    /// without an age check it would be trusted forever, on exactly the stock most likely to have
    /// silently gone. It asks; it never corrects.</para></summary>
    Unattested,
}
