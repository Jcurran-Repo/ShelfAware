namespace ShelfAware.Core.Domain;

/// <summary>
/// The ONE rule for moving a counted product's stock (DESIGN.md §13.2). Every road that changes how much
/// is on hand goes through here — a confirmed receipt, its removal, a chat purchase — because the
/// invariant that matters is symmetry: whatever a confirm adds, its undo takes back. <see cref="Add"/>
/// and <see cref="Remove"/> are the same operation with a sign, so that symmetry is structural rather
/// than two implementations agreeing by luck.
/// </summary>
public static class StockLedger
{
    /// <summary>Stock arrived: a receipt line was confirmed, or a purchase was recorded by hand.</summary>
    public static void Add(Product product, decimal quantity) => Move(product, quantity);

    /// <summary>Stock un-arrived: the receipt that recorded it was removed. Takes back exactly what
    /// <see cref="Add"/> put in.</summary>
    public static void Remove(Product product, decimal quantity) => Move(product, -quantity);

    private static void Move(Product product, decimal delta)
    {
        // Not counted: this household never asked for a number on this item, and inventing one on the
        // first receipt would opt them in silently.
        if (!product.TrackQuantity) return;

        // ⚠️ Counted but never COUNTED: stays unknown. A receipt says what you ADDED, not what you HAVE
        // — treating null as zero would turn "bought 3" into the confident claim "you have 3" for a
        // household that might have nine more behind them, which is the exact error the whole feature
        // exists to correct. One human count establishes the baseline; automated movement maintains it
        // from there, and until then null is the honest answer.
        if (product.QuantityOnHand is not { } onHand) return;

        // Clamped at zero, because negative stock isn't a thing a cupboard can hold. This is the one
        // place exact symmetry can break, and only when the count had already been driven below what's
        // being taken back — i.e. it was already out of step with the history, and zero is a better
        // answer there than a negative number.
        product.QuantityOnHand = Math.Max(0m, onHand + delta);

        // QuantityCountedAt is deliberately NOT touched. It records when a HUMAN last vouched for the
        // number, and the gap between that date and today is what lets the engine notice a count going
        // stale (§13.5). Stamping it here would make every receipt look like a fresh count and quietly
        // disable the drift check.
    }
}
