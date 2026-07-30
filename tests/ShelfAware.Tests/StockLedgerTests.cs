using ShelfAware.Core.Domain;

namespace ShelfAware.Tests;

public class StockLedgerTests
{
    private static Product Counted(decimal? onHand) =>
        new() { Name = "Beef Chuck Roast", TrackQuantity = true, QuantityOnHand = onHand };

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Typing_a_count_opts_the_product_in_and_stamps_the_attestation()
    {
        // Typing a number IS asking for the item to be counted — making you find a separate switch first
        // would be ceremony. And unlike automated movement, a human's count moves the date the staleness
        // check reads.
        var product = new Product { Name = "Beef Chuck Roast" };

        var assertedOut = StockLedger.Attest(product, 6, Now);

        Assert.True(product.TrackQuantity);
        Assert.Equal(6m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
        Assert.False(assertedOut);
    }

    [Fact]
    public void A_human_saying_zero_is_an_asserted_out()
    {
        // §13.4: real evidence, so the caller owes an OutNow — dated by running out rather than by
        // remembering to report it, which makes it BETTER burn-rate data than the button.
        var product = Counted(2);

        Assert.True(StockLedger.Attest(product, 0, Now));
        Assert.Equal(0m, product.QuantityOnHand);
    }

    [Fact]
    public void Arithmetic_arriving_at_zero_asserts_nothing()
    {
        // The other half of §13.4, and the one that protects the cadence engine: an approximate "Ate it"
        // decrement can empty a count, and that must not mint an OutNow the human never gave. The UI
        // asks; only the answer writes.
        var product = Counted(1);

        StockLedger.Remove(product, 1); // returns void — there is no path here that reports an outage

        Assert.Equal(0m, product.QuantityOnHand);
    }

    [Fact]
    public void A_negative_attested_count_is_clamped_not_stored()
    {
        // Both halves matter: it reports the asserted-out AND the shelf reads zero — a cupboard can't
        // hold minus two. (The first version of this test checked only the return value, so a stored
        // -2 would have passed it.)
        var product = Counted(3);

        Assert.True(StockLedger.Attest(product, -2, Now));
        Assert.Equal(0m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
    }

    [Fact]
    public void Stopping_keeps_the_number_dormant_rather_than_forgetting_it()
    {
        // The v3.6 toggle semantics: off is dormant, not destructive. "You counted 6 on this date"
        // stays a true historical fact; what stops is the BELIEVING, and every reader gates on
        // TrackQuantity, so the dormant pair influences nothing until a fresh count.
        var product = Counted(6);
        product.QuantityCountedAt = Now;

        StockLedger.StopCounting(product);

        Assert.False(product.TrackQuantity);
        Assert.Equal(6m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
    }

    [Fact]
    public void A_dormant_count_is_frozen_at_its_date_not_maintained()
    {
        // Receipts must not keep a stopped count fresh-looking: Move gates on TrackQuantity, so the
        // number stays exactly what it was when counting stopped.
        var product = Counted(6);
        StockLedger.StopCounting(product);

        StockLedger.Add(product, 3);

        Assert.Equal(6m, product.QuantityOnHand);
    }

    [Fact]
    public void A_relative_adjust_moves_the_number_but_not_the_clock()
    {
        // "Used two" states a DELTA, not a level — the person saw what they took, not the rows behind
        // it. Stamping here would let a weekly "Used one" renew the count's credibility forever
        // without anyone looking, and §13.5's drift check would never fire for the most engaged users.
        var counted = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var product = Counted(4);
        product.QuantityCountedAt = counted;

        var assertedOut = StockLedger.AdjustByHuman(product, -2, Now);

        Assert.False(assertedOut);
        Assert.Equal(2m, product.QuantityOnHand);
        Assert.Equal(counted, product.QuantityCountedAt);
    }

    [Fact]
    public void A_relative_adjust_landing_at_zero_stamps_and_asserts_the_out()
    {
        // Taking the last package IS seeing the shelf empty — a level statement, so the clock moves
        // and the caller owes the OutNow. The clamped case included: "used two" against one is none.
        var counted = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var product = Counted(1);
        product.QuantityCountedAt = counted;

        var assertedOut = StockLedger.AdjustByHuman(product, -2, Now);

        Assert.True(assertedOut);
        Assert.Equal(0m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
    }

    [Fact]
    public void A_dormant_count_cannot_be_adjusted_frozen_means_frozen()
    {
        // Found by the 7/30 audit: "stop counting the rice" then a habitual "used two" was editing the
        // frozen historical number — the one the product page attributes as "you counted 6 on this
        // date". A delta must not edit history; resuming starts from a fresh Attest.
        var product = Counted(6);
        product.QuantityCountedAt = Now;
        StockLedger.StopCounting(product);

        Assert.False(StockLedger.AdjustByHuman(product, -2, Now.AddDays(1)));
        Assert.Equal(6m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
        Assert.False(product.TrackQuantity);
    }

    [Fact]
    public void A_fresh_count_resumes_a_dormant_product()
    {
        // The way back in, and the counterweight to the dormancy gate above: deltas refuse, but a
        // LOOK opts back in — otherwise stopping would be a one-way door and the chat reply's "say a
        // fresh count and I'll start again" would be a promise nothing keeps.
        var product = Counted(6);
        StockLedger.StopCounting(product);

        Assert.False(StockLedger.Attest(product, 4, Now));
        Assert.True(product.TrackQuantity);
        Assert.Equal(4m, product.QuantityOnHand);
        Assert.Equal(Now, product.QuantityCountedAt);
    }

    [Fact]
    public void A_relative_adjust_against_an_unknown_count_does_nothing()
    {
        // No baseline, nothing to be relative to — the callers refuse first; the ledger holds the
        // same line rather than trusting them to.
        var product = Counted(onHand: null);

        Assert.False(StockLedger.AdjustByHuman(product, -1, Now));
        Assert.Null(product.QuantityOnHand);
        Assert.Null(product.QuantityCountedAt);
    }

    [Fact]
    public void A_purchase_adds_the_quantity_actually_bought()
    {
        var product = Counted(2);

        StockLedger.Add(product, 3);

        Assert.Equal(5m, product.QuantityOnHand);
    }

    [Fact]
    public void Removing_a_receipt_takes_back_exactly_what_it_added()
    {
        // The invariant §13.2 exists for: confirm-then-remove returns the count to where it started.
        var product = Counted(5);

        StockLedger.Add(product, 3);
        StockLedger.Remove(product, 3);

        Assert.Equal(5m, product.QuantityOnHand);
    }

    [Fact]
    public void A_product_nobody_counts_is_left_alone()
    {
        // Opt-in means opt-in: a receipt must not silently start counting an item for a household that
        // never asked, so this stays null rather than becoming 3.
        var product = new Product { Name = "Bananas", TrackQuantity = false };

        StockLedger.Add(product, 3);

        Assert.Null(product.QuantityOnHand);
    }

    [Fact]
    public void Counted_but_never_counted_stays_unknown()
    {
        // ⚠️ The rule most likely to be "simplified" into a bug. A receipt says what you ADDED, not what
        // you HAVE — turning null into 3 would claim a total for a freezer that might hold nine more
        // behind them, which is the exact error the counting feature exists to correct. A human count
        // establishes the baseline; receipts maintain it from there.
        var product = Counted(onHand: null);

        StockLedger.Add(product, 3);

        Assert.Null(product.QuantityOnHand);
    }

    [Fact]
    public void The_count_never_goes_negative()
    {
        // A cupboard can't hold minus two. This is the one place exact symmetry breaks, and only when
        // the count had already been driven below what's being taken back.
        var product = Counted(1);

        StockLedger.Remove(product, 3);

        Assert.Equal(0m, product.QuantityOnHand);
    }

    [Fact]
    public void Weight_items_keep_their_fractions()
    {
        var product = Counted(1.24m);

        StockLedger.Add(product, 2.34m);

        Assert.Equal(3.58m, product.QuantityOnHand);
    }

    [Fact]
    public void Movement_never_counts_as_a_human_having_looked()
    {
        // QuantityCountedAt is an ATTESTATION date. Stamping it here would make every receipt look like
        // a fresh count and quietly disable the staleness check that reads the gap (§13.5).
        var counted = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var product = Counted(2);
        product.QuantityCountedAt = counted;

        StockLedger.Add(product, 3);
        StockLedger.Remove(product, 1);

        Assert.Equal(counted, product.QuantityCountedAt);
    }
}
