using ShelfAware.Core.Ingest;

namespace ShelfAware.Tests;

/// <summary>
/// The receipt-reminder cadence: a household is nudged against its OWN upload rhythm, never a constant.
/// These pin the four ways the feature could go wrong in a way no user would report as a bug — nagging a
/// household that has just uploaded, nagging one that has barely started, learning a rhythm from a batch
/// of receipts carried in together, and letting one holiday reset what "usual" means.
/// </summary>
public class UploadCadenceTests
{
    private static readonly DateOnly Day0 = new(2026, 3, 1);

    private static DateOnly D(int d) => Day0.AddDays(d);

    /// <summary>An upload on day <paramref name="d"/>, mid-afternoon — the time of day never matters, only
    /// the local day, and a fixed hour keeps a fixture from straddling midnight.</summary>
    private static DateTimeOffset Up(int d) => new(D(d).ToDateTime(new TimeOnly(15, 0)));

    private static IReadOnlyList<DateTimeOffset> Uploads(params int[] days) => [.. days.Select(Up)];

    [Fact]
    public void A_household_on_a_weekly_rhythm_is_reminded_one_day_after_its_own_gap()
    {
        // Uploads every 7 days; the last was day 21. Day 28 is exactly the usual gap — still silent.
        var uploads = Uploads(0, 7, 14, 21);

        Assert.Null(UploadCadence.Evaluate(uploads, D(28)));

        var reminder = UploadCadence.Evaluate(uploads, D(29));
        Assert.NotNull(reminder);
        Assert.Equal(8, reminder.DaysSinceLastUpload);
        Assert.Equal(7, reminder.UsualGapDays);
        Assert.Equal(8, reminder.RemindAfterDays);
        Assert.Equal(D(21), reminder.LastUploadedOn);
    }

    [Fact]
    public void A_new_household_is_never_nagged()
    {
        // Three upload days is two gaps — not a rhythm, and guessing at one in order to nag someone in
        // their first week is the worst first impression the feature could make.
        var uploads = Uploads(0, 7, 14);

        Assert.Null(UploadCadence.Evaluate(uploads, D(90)));
        Assert.Null(UploadCadence.Evaluate([], D(90)));

        // A fourth upload day is the third gap, and the reminder becomes possible.
        Assert.NotNull(UploadCadence.Evaluate(Uploads(0, 7, 14, 21), D(90)));
    }

    [Fact]
    public void A_stack_of_receipts_carried_in_together_counts_as_one_upload()
    {
        // Four receipts from one trip, uploaded back to back, plus three earlier weekly ones. Counting
        // them as four upload days would put three zero-day gaps into the median and drag the learned
        // cadence to nothing — nagging a household every other day for having been thorough.
        var sameDay = new[] { Up(21), Up(21).AddMinutes(3), Up(21).AddMinutes(6), Up(21).AddMinutes(9) };
        var uploads = Uploads(0, 7, 14).Concat(sameDay).ToList();

        Assert.Null(UploadCadence.Evaluate(uploads, D(28)));   // still on the weekly rhythm, not a daily one
        var reminder = UploadCadence.Evaluate(uploads, D(29));
        Assert.NotNull(reminder);
        Assert.Equal(7, reminder.UsualGapDays);
    }

    [Fact]
    public void One_long_holiday_does_not_become_the_usual_gap()
    {
        // Weekly, then a 28-day break, then weekly again. A mean would read 11 days and stay wrong for
        // months; the median goes back to describing the ordinary week as soon as the ordinary weeks resume.
        var uploads = Uploads(0, 7, 14, 42, 49, 56, 63);

        Assert.Equal(7, UploadCadence.Evaluate(uploads, D(71))!.UsualGapDays);
        Assert.Null(UploadCadence.Evaluate(uploads, D(70)));   // 7 days quiet — inside the rhythm
    }

    [Fact]
    public void A_household_that_uploads_most_days_is_reminded_after_three_days_not_two()
    {
        // Median gap 1 → the usual-gap-plus-a-day rule alone would fire on day 2, which is nagging.
        // The floor holds it to three quiet days; the reminder still says what the rhythm actually is.
        var uploads = Uploads(0, 1, 2, 3);

        Assert.Null(UploadCadence.Evaluate(uploads, D(5)));    // two quiet days
        var reminder = UploadCadence.Evaluate(uploads, D(6));
        Assert.NotNull(reminder);
        Assert.Equal(1, reminder.UsualGapDays);
        Assert.Equal(3, reminder.RemindAfterDays);
        Assert.Equal(UploadCadence.MinimumQuietDays, reminder.RemindAfterDays);
    }

    [Fact]
    public void An_even_number_of_gaps_rounds_the_median_up_to_a_whole_day()
    {
        // Gaps 4, 5, 6, 9 → the two middles are 5 and 6, so the median is 5.5. The banner speaks in whole
        // days, and rounding up waits the longer of the two middles — the side that under-nags.
        var uploads = Uploads(0, 4, 9, 15, 24);

        var reminder = UploadCadence.Evaluate(uploads, D(31));
        Assert.NotNull(reminder);
        Assert.Equal(6, reminder.UsualGapDays);
        Assert.Equal(7, reminder.RemindAfterDays);
        Assert.Null(UploadCadence.Evaluate(uploads, D(30)));
    }

    [Fact]
    public void A_dismissal_snoozes_for_one_more_of_the_households_own_gaps()
    {
        var uploads = Uploads(0, 7, 14, 21);
        var reminder = UploadCadence.Evaluate(uploads, D(29));
        Assert.NotNull(reminder);
        Assert.Equal(D(36), reminder.SnoozeUntil);   // dismissed on day 29, hidden through day 36

        // Hidden through the snooze day itself, back the morning after — the household has now been
        // quiet for two of its own gaps, which is worth saying again.
        Assert.Null(UploadCadence.Evaluate(uploads, D(29), snoozedUntil: D(36)));
        Assert.Null(UploadCadence.Evaluate(uploads, D(36), snoozedUntil: D(36)));
        Assert.NotNull(UploadCadence.Evaluate(uploads, D(37), snoozedUntil: D(36)));
    }

    [Fact]
    public void An_upload_clears_the_reminder_without_touching_the_snooze()
    {
        // The household uploads on day 29 instead of dismissing. The next reminder is due on day 37 at
        // the earliest, which is past any snooze a dismissal on day 29 could have written — so a stale
        // snooze can never swallow the reminder after the rhythm has moved on.
        var uploads = Uploads(0, 7, 14, 21, 29);

        Assert.Null(UploadCadence.Evaluate(uploads, D(30), snoozedUntil: D(36)));
        Assert.NotNull(UploadCadence.Evaluate(uploads, D(37), snoozedUntil: D(36)));
    }

    [Fact]
    public void A_receipt_stamped_in_the_future_produces_no_reminder()
    {
        // A restored backup or a box whose clock was wrong. A negative quiet stretch is not a sentence
        // the banner can say, and every number derived from it would be nonsense.
        var uploads = Uploads(0, 7, 14, 21);

        Assert.Null(UploadCadence.Evaluate(uploads, D(20)));
    }
}
