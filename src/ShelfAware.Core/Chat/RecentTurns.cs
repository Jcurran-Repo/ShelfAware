namespace ShelfAware.Core.Chat;

/// <summary>The last few exchanges of one conversation with Reginald — what lets "change it to a large"
/// resolve "it" against the drink he just logged. Bounded on purpose: a box people type one-liners into
/// needs the thread of the last moment, not a transcript, and every turn kept is replayed (and paid for)
/// on every turn after it.</summary>
public sealed class RecentTurns
{
    private readonly int _keep;
    private readonly List<ChatTurn> _turns = [];

    /// <param name="keep">How many exchanges to keep — the oldest is dropped when another arrives.</param>
    public RecentTurns(int keep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        _keep = keep;
    }

    /// <summary>The kept exchanges, oldest first — the order <see cref="IPantryChat.HandleAsync"/> replays them.</summary>
    public IReadOnlyList<ChatTurn> Turns => _turns;

    /// <summary>Keep one finished exchange.</summary>
    public void Add(string user, string assistant)
    {
        _turns.Add(new ChatTurn(user, assistant));
        if (_turns.Count > _keep) _turns.RemoveAt(0);
    }
}
