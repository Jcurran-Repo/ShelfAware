using ShelfAware.Core.Chat;

namespace ShelfAware.Tests;

/// <summary>The short memory a one-line chat box keeps, so "change it" resolves against the last turn.</summary>
public class RecentTurnsTests
{
    [Fact]
    public void Turns_are_kept_oldest_first_and_the_oldest_drops_when_another_arrives()
    {
        var turns = new RecentTurns(keep: 2);
        Assert.Empty(turns.Turns);

        turns.Add("a chiller", "Which size?");
        turns.Add("large", "Logged [entry 7] chiller, large.");
        Assert.Equal([new ChatTurn("a chiller", "Which size?"), new ChatTurn("large", "Logged [entry 7] chiller, large.")], turns.Turns);

        turns.Add("make it 900", "Updated [entry 7].");
        Assert.Equal([new ChatTurn("large", "Logged [entry 7] chiller, large."), new ChatTurn("make it 900", "Updated [entry 7].")], turns.Turns);
    }

    [Fact]
    public void One_turn_kept_is_just_the_last()
    {
        var turns = new RecentTurns(keep: 1);
        turns.Add("a", "b");
        turns.Add("c", "d");

        Assert.Equal([new ChatTurn("c", "d")], turns.Turns);
    }

    [Fact]
    public void Turns_read_before_another_exchange_finishes_do_not_change_under_the_reader()
    {
        var turns = new RecentTurns(keep: 1);
        turns.Add("a", "b");
        var sent = turns.Turns;

        turns.Add("c", "d");

        Assert.Equal([new ChatTurn("a", "b")], sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Keeping_nothing_is_refused(int keep)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentTurns(keep));
    }
}
