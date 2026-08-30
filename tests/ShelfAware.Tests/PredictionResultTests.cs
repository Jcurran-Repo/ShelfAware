using ShelfAware.Core.Prediction;

namespace ShelfAware.Tests;

public class PredictionResultTests
{
    private static PredictionResult With(double? rebuy, double? burn) =>
        new() { ProductId = 1, Status = PredictionStatus.Stocked, Basis = "", RebuyIntervalDays = rebuy, BurnRateDays = burn };

    [Fact]
    public void RunsOutEarly_fires_at_exactly_the_threshold()
    {
        Assert.True(With(rebuy: 10, burn: 7).RunsOutEarly);     // gap 3 == the 3-day threshold, inclusive
        Assert.False(With(rebuy: 9, burn: 7).RunsOutEarly);     // gap 2 — below
        Assert.False(With(rebuy: 10, burn: null).RunsOutEarly); // no burn rhythm -> null gap -> never fires
    }
}
