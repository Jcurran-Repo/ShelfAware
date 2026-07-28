using ShelfAware.Core.Prediction;

namespace ShelfAware.Core.Reporting;

/// <summary>One product's buying history, flattened for the backlog check — the Web layer joins EF
/// rows into these so the analysis stays EF-free (the same seam shape as <see cref="PurchaseFact"/>).</summary>
/// <param name="PurchaseDates">Every date this was bought. Normalized (distinct + ascending) inside
/// <see cref="BacklogSignals.Find"/>, so a caller may pass raw event dates.</param>
/// <param name="OutageDates">Every date an OutNow was signaled for it. Restocks are not outages — the
/// same rule the predictor's burn cycles follow.</param>
/// <param name="PricedSpend">What the priced purchases cost. A purchase the app has no price for is
/// counted in <paramref name="UnpricedPurchases"/> rather than guessed at, so the report can disclose
/// the gap instead of understating it silently.</param>
/// <param name="RebuyIntervalDays">The item's learned rebuy rhythm (<c>PredictionResult.RebuyIntervalDays</c>),
/// or null when it hasn't got one. Not recomputed here — the engine owns that number.</param>
/// <param name="RecentMealUses">How many logged meals in the caller's recent window listed this product
/// as a main ingredient. REPORTED, never scored: it's evidence the item is moving, but the meal log
/// only sees cooking that went through a saved recipe, so its absence proves nothing.</param>
public sealed record BacklogInput(
    int ProductId,
    string ProductName,
    IReadOnlyList<DateOnly> PurchaseDates,
    IReadOnlyList<DateOnly> OutageDates,
    decimal TotalQuantity,
    decimal PricedSpend,
    int UnpricedPurchases,
    double? RebuyIntervalDays,
    int RecentMealUses);

/// <summary>One item worth checking: bought repeatedly, never once reported out, and now past its own
/// rebuy rhythm without a restock.</summary>
/// <param name="Trips">Distinct purchase DATES — occasions, not line items. Same-day buys collapse
/// exactly as they do in the engine, because that's what the cycle pairing sees.</param>
/// <param name="SpanDays">First buy to today: the whole stretch it was restocked over without ever
/// being reported out. The claim gets stronger the longer this is.</param>
/// <param name="OverdueDays">How far past the rebuy rhythm the item is — the size of the silence this
/// report is pointing at.</param>
/// <param name="SpendIncomplete">Whether some of its purchases had no price, so <paramref name="Spend"/>
/// is a floor rather than the total.</param>
public sealed record BacklogFinding(
    int ProductId,
    string ProductName,
    int Trips,
    DateOnly FirstBought,
    DateOnly LastBought,
    int DaysSinceLastBought,
    int SpanDays,
    double RebuyIntervalDays,
    int OverdueDays,
    decimal TotalQuantity,
    decimal Spend,
    bool SpendIncomplete,
    int RecentMealUses);

/// <param name="Considered">How many products had enough buying history to judge — the denominator
/// behind "N of M", so a short list reads as a finding rather than an empty page.</param>
/// <param name="EverRanOut">How many of those have actually closed a burn cycle at least once.</param>
public sealed record BacklogReport(
    IReadOnlyList<BacklogFinding> Findings,
    int Considered,
    int EverRanOut)
{
    /// <summary>What share of judgable products have ever really been reported out — how much evidence
    /// the "never ran out" half of the test is working from. Measured and DISCLOSED rather than used as
    /// a gate: at low coverage the silence means less, but the overdue half still stands on buying
    /// behaviour alone, so a finding stays worth showing. The UI says which it's leaning on.</summary>
    public double OutageCoverage => Considered == 0 ? 0 : (double)EverRanOut / Considered;
}

/// <summary>
/// The backlog check (DESIGN.md §13.7): which items keep getting restocked, have never once been
/// reported run out, AND have now gone quiet past their own rebuy rhythm. The signature is already
/// sitting in data the household collected for other reasons, so this costs no schema and no data
/// entry, and its job is to name the handful of products worth turning a real count on for.
/// <para><b>Why all three conditions.</b> "Never ran out" alone does not discriminate: measured against
/// real data it flagged 26 of 27 regularly-bought products, because a household that rarely taps
/// <c>OutNow</c> leaves every item silent. The buying pattern is the half that doesn't depend on a
/// button — an item you bought like clockwork and then STOPPED rebuying, without ever saying you ran
/// out, is one you are most likely still eating through. That cut the same real household to 2.</para>
/// <para>The deliberate limit, and the reason nothing here asserts a quantity: none of this is proof.
/// A missing OutNow may only mean nobody taps the button, and a rhythm that stopped may only mean
/// tastes changed. So the finding is "worth checking, and here is what you've put into it" — never
/// "you have six" — the same discipline <see cref="ExpirationOutcomes"/> holds for waste.</para>
/// </summary>
public static class BacklogSignals
{
    /// <summary>Buys needed before "never ran out" is a pattern rather than a coincidence. Two is one
    /// rebuy, which reads the same whether you hoard it or simply bought it twice; three is the first
    /// count where the silence is about the item.</summary>
    public const int MinPurchases = 3;

    public static BacklogReport Find(IEnumerable<BacklogInput> inputs, DateOnly today)
    {
        var considered = 0;
        var everRanOut = 0;
        var findings = new List<BacklogFinding>();

        foreach (var input in inputs)
        {
            var dates = input.PurchaseDates.Distinct().OrderBy(d => d).ToList();
            if (dates.Count < MinPurchases) continue;
            considered++;

            // The engine's own definition of a completed cycle — asked for its COUNT, which is the one
            // thing BurnRateDays can't report (it's null at zero cycles and at one alike).
            var outages = input.OutageDates.Distinct().OrderBy(d => d).ToList();
            if (ReplenishmentPredictor.BurnCycles(dates, outages).Count > 0)
            {
                everRanOut++;
                continue;
            }

            // The half that needs no button: it was on a rhythm, and the rhythm stopped. Without a
            // learned rhythm there's nothing to have gone quiet against, so there's nothing to say.
            if (input.RebuyIntervalDays is not { } rebuy) continue;
            var sinceLast = today.DayNumber - dates[^1].DayNumber;
            var overdue = sinceLast - (int)Math.Floor(rebuy);
            if (overdue <= 0) continue;

            findings.Add(new BacklogFinding(
                input.ProductId,
                input.ProductName,
                Trips: dates.Count,
                FirstBought: dates[0],
                LastBought: dates[^1],
                DaysSinceLastBought: sinceLast,
                SpanDays: today.DayNumber - dates[0].DayNumber,
                RebuyIntervalDays: rebuy,
                OverdueDays: overdue,
                input.TotalQuantity,
                Spend: input.PricedSpend,
                SpendIncomplete: input.UnpricedPurchases > 0,
                input.RecentMealUses));
        }

        // Ranked by money committed to an item that has never once been reported out — one number, in
        // dollars, that already carries both how often you buy it and what it costs. Trips break ties as
        // the evidence column: same money, more buys, stronger pattern.
        return new BacklogReport(
            findings
                .OrderByDescending(f => f.Spend)
                .ThenByDescending(f => f.Trips)
                .ThenBy(f => f.ProductName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            considered,
            everRanOut);
    }
}
