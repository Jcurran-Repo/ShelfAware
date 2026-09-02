using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Tests;

/// <summary>The /about wishlist store (auth.db operator data): what a submission records, how the SOFT
/// interest total and the TRUSTED distinct-email count differ, how the notify list dedups per email, and
/// how the retention trim sheds anonymous clicks before it ever costs a real address.</summary>
public class WishlistStoreTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private WishlistStore Store() => new(_authDb);

    [Fact]
    public async Task Records_an_anonymous_interest_click()
    {
        var store = Store();
        await store.RecordAsync("aware", null, DateTimeOffset.Now);

        Assert.Equal(1, await store.InterestCountAsync());
        var summary = await store.SummarizeAsync();
        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.DistinctEmails); // no address given
        Assert.Equal(1, summary.ByTier["aware"]);
        Assert.Empty(await store.ContactsAsync());
    }

    [Fact]
    public async Task A_blank_or_whitespace_email_is_stored_as_null_not_a_contact()
    {
        var store = Store();
        await store.RecordAsync("shelf", "   ", DateTimeOffset.Now);

        Assert.Empty(await store.ContactsAsync());
        Assert.Equal(0, (await store.SummarizeAsync()).DistinctEmails);
    }

    [Fact]
    public async Task Distinct_emails_are_counted_case_insensitively_while_the_soft_total_counts_every_submission()
    {
        var store = Store();
        await store.RecordAsync("aware", "Jordan@Example.com", DateTimeOffset.Now);
        await store.RecordAsync("aware", "jordan@example.com", DateTimeOffset.Now); // same person, different case
        await store.RecordAsync("shelf", "wife@example.com", DateTimeOffset.Now);

        var summary = await store.SummarizeAsync();
        Assert.Equal(3, summary.Total);          // the soft count is every click
        Assert.Equal(2, summary.DistinctEmails); // the trusted count folds the case-dupe
    }

    [Fact]
    public async Task Contacts_are_one_per_email_with_the_latest_tier_newest_first()
    {
        var store = Store();
        var older = DateTimeOffset.Now.AddDays(-2);
        var newest = DateTimeOffset.Now;
        await store.RecordAsync("shelf", "a@example.com", older);
        await store.RecordAsync("aware", "a@example.com", newest);          // changed their mind → latest wins
        await store.RecordAsync("shelf", "b@example.com", older.AddDays(-1));

        var contacts = await store.ContactsAsync();

        Assert.Equal(2, contacts.Count);           // one row per distinct email
        Assert.Equal("a@example.com", contacts[0].Email); // newest first
        Assert.Equal("aware", contacts[0].Tier);          // the later choice, not the first
    }

    [Fact]
    public void The_trim_sheds_anonymous_rows_before_emailed_ones_oldest_first()
    {
        // Pure selection (DoomedIds) so the ordering is pinned without inserting MaxRows+1 rows.
        var rows = new List<WishlistStore.TrimRow>
        {
            new(1, "keep@example.com", DateTimeOffset.Now.AddDays(-10)), // emailed AND oldest — must survive longest
            new(2, null, DateTimeOffset.Now.AddDays(-9)),                // anonymous, older
            new(3, null, DateTimeOffset.Now.AddDays(-1)),                // anonymous, newer
            new(4, "b@example.com", DateTimeOffset.Now),                 // emailed, newest
        };

        // Over by 2: both anonymous rows go (oldest anonymous first); no email is touched.
        Assert.Equal([2, 3], WishlistStore.DoomedIds(rows, 2));
        // Over by 3: after the anonymous pair, the OLDEST emailed row is next — never a newer one.
        Assert.Equal([2, 3, 1], WishlistStore.DoomedIds(rows, 3));
    }
}
