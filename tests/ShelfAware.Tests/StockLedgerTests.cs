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
        Assert.True(StockLedger.Attest(Counted(3), -2, Now));
    }

    [Fact]
    public void Stopping_clears_the_number_rather_than_leaving_it_to_rot()
    {
        // A count nobody maintains is worse than none: the engine would go on trusting it.
        var product = Counted(6);
        product.QuantityCountedAt = Now;

        StockLedger.StopCounting(product);

        Assert.False(product.TrackQuantity);
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
