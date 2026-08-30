using ShelfAware.Core.Prediction;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Tests;

/// <summary>
/// The forecast arithmetic, testable because it no longer lives inside SpendInsight.razor — this is the
/// only place a COUNT moves a number denominated in money, and a regression here reads to the household
/// as their own spending changing.
/// </summary>
public class SpendForecastTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 1);

    private static DateOnly D(int d) => Day0.AddDays(d);

    private static PredictionResult Prediction(DateOnly? due, bool suppressed = false, DateOnly? runsOut = null) =>
        new()
        {
            ProductId = 1, Status = PredictionStatus.Stocked, DueDate = due, Basis = "test",
            SuppressedByCount = suppressed, CountRunsOutOn = runsOut,
        };

    [Fact]
    public void FirstBuy_IsTheDueDate_ForAnUncountedItem()
    {
        Assert.Equal(D(10), SpendForecast.FirstBuy(Prediction(D(10))));
    }

    [Fact]
    public void FirstBuy_IsTheExhaustionDate_WhenACountIsHoldingItBack()
    {
        // The whole point: suppression leaves DueDate alone so surfaces can explain themselves, so a
        // forecast that stepped from it would bill for a purchase the app is telling them not to make.
        var p = Prediction(D(10), suppressed: true, runsOut: D(90));

        Assert.Equal(D(90), SpendForecast.FirstBuy(p));
    }

    [Fact]
    public void FirstBuy_FallsBackToTheDueDate_WhenSuppressedWithNoExhaustionDate()
    {
        // Reachable: an item with no typical trip quantity gets suppression but no horizon.
        var p = Prediction(D(10), suppressed: true, runsOut: null);

        Assert.Equal(D(10), SpendForecast.FirstBuy(p));
    }

    [Fact]
    public void FirstBuy_IsNull_WhenTheItemIsStillLearning()
    {
        Assert.Null(SpendForecast.FirstBuy(Prediction(due: null)));
    }

    [Fact]
    public void InWindow_ChargesEveryLandingInsideTheWindow()
    {
        // First buy D(10), every 10 days, window D(20)-D(45) → D(20), D(30), D(40) = 3 buys.
        var total = SpendForecast.InWindow(
            D(10), intervalDays: 10, today: D(5), windowStart: D(20), windowEnd: D(45), costPerBuy: 4m);

        Assert.Equal(12m, total);
    }

    [Fact]
    public void InWindow_ChargesNothingWhenTheWindowClosesBeforeTheFirstBuy()
    {
        var total = SpendForecast.InWindow(
            D(50), intervalDays: 10, today: D(5), windowStart: D(0), windowEnd: D(20), costPerBuy: 4m);

        Assert.Equal(0m, total);
    }

    [Fact]
    public void InWindow_ChargesNothingWhenEveryLandingStraddlesTheWindow()
    {
        // Steps land on D(100) and D(110); the window sits between them. A forecast must not round a
        // near-miss into a purchase.
        var total = SpendForecast.InWindow(
            D(10), intervalDays: 10, today: D(5), windowStart: D(101), windowEnd: D(105), costPerBuy: 4m);

        Assert.Equal(0m, total);
    }

    [Fact]
    public void InWindow_SkipsDaysAlreadyPast_RatherThanBackChargingThem()
    {
        // A due date the household already sailed past is a reminder, not a second purchase. Window
        // D(0)-D(45), today D(25): only D(30) and D(40) are still ahead.
        var total = SpendForecast.InWindow(
            D(10), intervalDays: 10, today: D(25), windowStart: D(0), windowEnd: D(45), costPerBuy: 4m);

        Assert.Equal(8m, total);
    }

    [Fact]
    public void InWindow_TreatsADegenerateIntervalAsOneDay_AndTerminates()
    {
        // A rounded-to-zero interval would otherwise never advance the cursor.
        var total = SpendForecast.InWindow(
            D(0), intervalDays: 0.2, today: D(-1), windowStart: D(0), windowEnd: D(2), costPerBuy: 1m);

        Assert.Equal(3m, total); // D(0), D(1), D(2)
    }

    [Fact]
    public void InWindow_ChargesALandingExactlyOnTheWindowStart()
    {
        // The charge test is `day >= windowStart`, inclusive: a buy that lands on the first window day
        // (and is still in the future) counts.
        var total = SpendForecast.InWindow(
            D(10), intervalDays: 100, today: D(9), windowStart: D(10), windowEnd: D(11), costPerBuy: 5m);

        Assert.Equal(5m, total); // the single D(10) landing
    }

    [Fact]
    public void InWindow_DoesNotChargeALandingExactlyOnToday()
    {
        // The charge test is strictly `day > today`: a purchase falling on today itself is one the app
        // is telling the household about NOW, not a future cost, so it isn't billed to the window.
        var total = SpendForecast.InWindow(
            D(10), intervalDays: 100, today: D(10), windowStart: D(0), windowEnd: D(11), costPerBuy: 5m);

        Assert.Equal(0m, total); // the only landing, D(10), is today -> excluded
    }

    [Fact]
    public void InWindow_IsBoundedByTheBackstopAgainstAnAbsurdWindow()
    {
        // The loop is normally bounded by the window; the steps < 4000 backstop caps a pathological
        // wide window with a 1-day step at 4000 charges rather than walking the whole 5000-day span.
        var total = SpendForecast.InWindow(
            D(1), intervalDays: 1, today: D(0), windowStart: D(1), windowEnd: D(5000), costPerBuy: 1m);

        Assert.Equal(4000m, total);
    }
}
