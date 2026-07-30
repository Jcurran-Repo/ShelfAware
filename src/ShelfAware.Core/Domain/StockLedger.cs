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
    /// <summary>A HUMAN states the count — typing it on the product page, answering the app's "still got
    /// them?", correcting a decrement. Unlike <see cref="Add"/>/<see cref="Remove"/> this is an
    /// attestation, so it stamps the date the staleness check reads, and it opts the product in: typing
    /// a number IS asking for it to be counted, and making you find a separate switch first would be
    /// ceremony for its own sake. <see cref="StopCounting"/> is the way back out.
    /// <para><b>Returns true when this is an ASSERTED ZERO</b> — a human saying "we're out". §13.4: that
    /// is real evidence and the caller owes an <c>OutNow</c> signal for it, which feeds the burn-rate
    /// rhythm exactly like the button does — better, even, because it's dated by running out rather than
    /// by remembering to report it. A zero that arithmetic merely ARRIVED at (see <see cref="Remove"/>)
    /// returns nothing and writes nothing.</para></summary>
    public static bool Attest(Product product, decimal quantity, DateTimeOffset at)
    {
        product.TrackQuantity = true;
        product.QuantityOnHand = Math.Max(0m, quantity);
        product.QuantityCountedAt = at;
        return product.QuantityOnHand == 0m;
    }

    /// <summary>A HUMAN adjusts the count by a delta — "used two", the lists' one-tap "Used one". They
    /// are at the cupboard, but they are stating what they TOOK, not what is there: taking one from the
    /// front verifies nothing about the rows behind it. So the number moves and the attestation clock
    /// does NOT — a household that dutifully taps "Used one" every week must not renew a count's
    /// credibility forever without anyone actually looking, or §13.5's drift check never fires for
    /// exactly the most engaged users. Only <see cref="Attest"/> — a look at the shelf — re-anchors it.
    /// <para><b>The one exception is landing at zero, and it returns true there.</b> Taking the last
    /// package IS seeing the shelf empty — a statement of the level, not just the delta — so it stamps
    /// the clock and the caller owes the OutNow, exactly as for an attested zero (§13.4). That includes
    /// the clamped case: "used two" against a count of one really is none.</para>
    /// <para>Requires an established count — a delta against "unknown" has no baseline, and inventing
    /// one is the error §13.2 exists to avoid. Callers refuse before reaching here; the null check is
    /// the ledger holding its own invariant.</para></summary>
    public static bool AdjustByHuman(Product product, decimal delta, DateTimeOffset at)
    {
        if (product.QuantityOnHand is not { } onHand) return false;
        var landed = Math.Max(0m, onHand + delta);
        product.QuantityOnHand = landed;
        if (landed != 0m) return false;
        product.QuantityCountedAt = at;
        return true;
    }

    /// <summary>Stop counting this item: the product returns to running on the learned cadence alone.
    /// <para>The number and its date are KEPT, dormant — the same toggle semantics as v3.6's expiration
    /// dates ("off is dormant, not destructive"). "You counted 14 on Mar 12" stays a true historical
    /// fact whether or not anyone maintains it; what stops is the BELIEVING, and every reader gates on
    /// <see cref="Product.TrackQuantity"/>, so a dormant pair renders nowhere and influences nothing
    /// (including <see cref="Move"/> — receipts leave it frozen at its date). Resuming still starts
    /// from a fresh <see cref="Attest"/> — the old number is stale by definition — but the product page
    /// can show what was known and when, instead of amnesia.</para></summary>
    public static void StopCounting(Product product)
    {
        product.TrackQuantity = false;
    }

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
