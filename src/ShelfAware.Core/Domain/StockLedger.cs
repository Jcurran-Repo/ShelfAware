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
    /// <summary>What a human's count amounted to, and therefore what the caller owes. Returned instead
    /// of a bare bool because a zero has two meanings, and the difference is not the caller's to invent.
    /// </summary>
    public enum CountOutcome
    {
        /// <summary>A number was recorded. Nothing further is owed.</summary>
        Recorded,

        /// <summary>The human stated ZERO on a product this household buys here. §13.4: real evidence,
        /// dated by running out rather than by remembering to report it, so the caller owes an
        /// <c>OutNow</c> — it is what the burn-rate rhythm learns from.</summary>
        AssertedOutage,

        /// <summary>The human stated ZERO on a product with NO purchase history. The number is recorded
        /// exactly as any other — it is their honest evidence of how many, which is what a count is for
        /// — but the <c>OutNow</c> is WITHHELD, and the caller must not write one.
        /// <para>⚠️ An outage signal needs a rhythm to argue with. With no purchases behind it nothing
        /// can ever re-anchor or clear it: not a receipt, not a later count (attesting touches no
        /// signals), so the product sits <c>Pinned</c>/<c>Overdue</c> at the top of the dashboard and
        /// the grocery list indefinitely — while teaching nothing either, since <c>BurnCycles</c> needs
        /// purchases to form a cycle. This is the exact complement of the line <c>PantryOnHand</c> draws
        /// from the other side: a machine's zero may not CLAIM an outage, and a human's zero may not be
        /// silently DISCARDED.</para></summary>
        ZeroWithoutRhythm,
    }

    /// <summary>A HUMAN states the count — typing it on the product page, answering the app's "still got
    /// them?", correcting a decrement, or reviewing a shelf photo. Unlike <see cref="Add"/>/<see
    /// cref="Remove"/> this is an attestation, so it stamps the date the staleness check reads, and it
    /// opts the product in: typing a number IS asking for it to be counted, and making you find a
    /// separate switch first would be ceremony for its own sake. <see cref="StopCounting"/> is the way
    /// back out.</summary>
    /// <param name="hasPurchaseHistory">Whether this household has ever bought this product. ⚠️ Passed
    /// in rather than read off <see cref="Product.Purchases"/> ON PURPOSE: the nav collection is not
    /// loaded at every call site (<c>EfPantryStore</c> reaches its product through <c>FindAsync</c>), so
    /// reading it here would silently answer "no history" for a product with years of it and withhold an
    /// outage that was owed. A required parameter makes every caller state the fact, and the compiler
    /// catches the next one that appears.
    /// <para>This lives here, and not in a caller, because it is a property of the DATA rather than of
    /// the surface. Implemented in the census alone, it immediately drifted: the product page kept
    /// writing the pin the census had just decided not to write, for the same act on the same
    /// product.</para></param>
    public static CountOutcome Attest(Product product, decimal quantity, DateTimeOffset at, bool hasPurchaseHistory)
    {
        product.TrackQuantity = true;
        product.QuantityOnHand = Math.Max(0m, quantity);
        product.QuantityCountedAt = at;
        return Judge(product.QuantityOnHand == 0m, hasPurchaseHistory);
    }

    private static CountOutcome Judge(bool isZero, bool hasPurchaseHistory) => (isZero, hasPurchaseHistory) switch
    {
        (false, _) => CountOutcome.Recorded,
        (true, true) => CountOutcome.AssertedOutage,
        (true, false) => CountOutcome.ZeroWithoutRhythm,
    };

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
    /// <param name="hasPurchaseHistory">See <see cref="Attest"/> — the same fact, for the same reason.
    /// Landing at zero is an assertion about the level, so it faces the same question about whether an
    /// outage signal can ever be cleared.</param>
    public static CountOutcome AdjustByHuman(Product product, decimal delta, DateTimeOffset at, bool hasPurchaseHistory)
    {
        // Dormant means FROZEN: a stopped count is a historical fact ("you counted 14 on Mar 12"), and
        // a delta must not edit history — resuming starts from a fresh Attest, never from arithmetic
        // against a number nobody is maintaining. Same structural gate Move holds.
        if (!product.TrackQuantity) return CountOutcome.Recorded;
        if (product.QuantityOnHand is not { } onHand) return CountOutcome.Recorded;
        var landed = Math.Max(0m, onHand + delta);
        product.QuantityOnHand = landed;
        if (landed != 0m) return CountOutcome.Recorded;
        product.QuantityCountedAt = at;
        return Judge(isZero: true, hasPurchaseHistory);
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
