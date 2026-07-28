using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class SignalDateTests
{
    [Fact]
    public void A_signal_keeps_the_day_it_was_recorded_on_whatever_zone_reads_it_later()
    {
        // The bug this closes is a day-shift, not a crash. 11pm on the 28th at UTC-5 IS the 28th — that's
        // the day the human marked the item out. `.LocalDateTime.Date` would re-read the same instant in
        // whatever zone the machine is in now and call it the 29th on any UTC deployment (the documented
        // Linux/Azure TZ gotcha), silently moving historical rows by a day.
        var lateEvening = new DateTimeOffset(2026, 7, 28, 23, 0, 0, TimeSpan.FromHours(-5));

        Assert.Equal(new DateOnly(2026, 7, 28), SignalDate.Of(lateEvening));
    }

    [Fact]
    public void The_same_instant_read_from_two_offsets_keeps_each_ones_own_day()
    {
        // Same moment, two recordings. Each keeps the calendar day it happened on where it happened,
        // which is what makes the reading stable across a deployment that moves timezone.
        var atMinusFive = new DateTimeOffset(2026, 7, 28, 23, 0, 0, TimeSpan.FromHours(-5));
        var sameInstantUtc = atMinusFive.ToOffset(TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 7, 28), SignalDate.Of(atMinusFive));
        Assert.Equal(new DateOnly(2026, 7, 29), SignalDate.Of(sameInstantUtc));
    }

    [Fact]
    public void Midnight_is_its_own_day()
    {
        Assert.Equal(
            new DateOnly(2026, 7, 28),
            SignalDate.Of(new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero)));
    }
}
