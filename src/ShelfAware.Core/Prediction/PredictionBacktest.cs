using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Prediction;

/// <summary>
/// Scores the prediction engine against the product's own history — the same idea as the extraction
/// eval, pointed at the other half of the app. For every repurchase with at least two prior purchase
/// dates, predict from ONLY the data available before that trip (walk-forward, no peeking), then
/// measure how far the projected due date landed from the real repurchase date. Pure C#: no LLM, no
/// I/O, fully unit-testable — the engine measuring itself with the same statistics it predicts with.
///
/// Honest caveat, by design: the engine predicts RUN-OUT; the observable ground truth is the next
/// REPURCHASE. For the rebuy rhythm they're the same thing; when the burn rate drives they can
/// legitimately differ (you may rebuy before you run out). The Accuracy page says so out loud.
/// </summary>
public static class PredictionBacktest
{
    public record ProductScore(int ProductId, string Name, int Samples, double MedianAbsErrorDays, int WithinTwoDays)
    {
        public double HitRate => Samples == 0 ? 0 : (double)WithinTwoDays / Samples;
    }

    public record Summary(
        int Products, int Samples, double MedianAbsErrorDays, int WithinTwoDays,
        IReadOnlyList<ProductScore> PerProduct)
    {
        public double HitRate => Samples == 0 ? 0 : (double)WithinTwoDays / Samples;
    }

    public static Summary Run(IEnumerable<Product> products)
    {
        var perProduct = new List<ProductScore>();
        var allAbsErrors = new List<int>();
        var withinTwo = 0;

        foreach (var product in products)
        {
            var dates = product.Purchases.Select(p => p.PurchasedAt).Distinct().OrderBy(d => d).ToList();
            if (dates.Count < 3) continue; // needs 2 prior dates to predict from + 1 actual to score

            var absErrors = new List<int>();
            for (var i = 2; i < dates.Count; i++)
            {
                var actual = dates[i];
                // Rebuild the product exactly as it looked the day before this trip.
                var snapshot = new Product
                {
                    Id = product.Id,
                    Name = product.Name,
                    Purchases = product.Purchases.Where(p => p.PurchasedAt < actual).ToList(),
                    Signals = product.Signals.Where(s => DateOnly.FromDateTime(s.SignaledAt.Date) < actual).ToList(),
                };
                // Deliberately expiration-blind (the honorExpirations default): the backtest scores the
                // LEARNED rhythm against real rebuy dates, and an expiry pin would overwrite DueDate
                // with a label fact — grading the model on answers it didn't predict.
                if (ReplenishmentPredictor.Predict(snapshot, dates[i - 1]).DueDate is not { } due) continue;
                absErrors.Add(Math.Abs(actual.DayNumber - due.DayNumber));
            }

            if (absErrors.Count == 0) continue;
            var hits = absErrors.Count(e => e <= 2);
            withinTwo += hits;
            allAbsErrors.AddRange(absErrors);
            perProduct.Add(new ProductScore(product.Id, product.Name, absErrors.Count, Median(absErrors), hits));
        }

        return new Summary(
            perProduct.Count,
            allAbsErrors.Count,
            allAbsErrors.Count > 0 ? Median(allAbsErrors) : 0,
            withinTwo,
            perProduct.OrderByDescending(p => p.Samples).ThenBy(p => p.Name).ToList());
    }

    private static double Median(IReadOnlyList<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
