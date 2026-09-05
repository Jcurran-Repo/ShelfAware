using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The service that ties the pure lookalike detector to Eggs's per-pair memory: it records when a pair was
/// first flagged (so his mood ages), honours a permanent "they're different" dismissal, and reverses one.
/// Real SQLite so the unique (household, lower, higher) index and the query-filter stamping are the ones
/// production runs.
/// </summary>
public class LookalikeNudgeServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly LookalikeNudgeService _service;

    public LookalikeNudgeServiceTests()
        => _service = new LookalikeNudgeService(_db, NullLogger<LookalikeNudgeService>.Instance);

    public void Dispose() => _db.Dispose();

    /// <summary>Three products, of which two ("Artesano Brioche Bread" / "Brioche Loaf") share the pair-unique
    /// word "brioche" — one lookalike pair; the milk shares nothing. Returned lowest-id first.</summary>
    private async Task<IReadOnlyList<Product>> SeedList()
    {
        await using var db = _db.CreateDbContext();
        db.Products.AddRange(
            new Product { Name = "Artesano Brioche Bread" },
            new Product { Name = "Brioche Loaf" },
            new Product { Name = "Whole Milk" });
        await db.SaveChangesAsync();
        return await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
    }

    [Fact]
    public async Task Flags_the_pair_and_records_when_it_was_first_seen()
    {
        var list = await SeedList();
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var nudge = Assert.Single(await _service.GetActiveAsync(list, now));

        Assert.Equal(NudgeMood.Fresh, nudge.Mood); // just spotted
        Assert.Equal(list[0].Id, nudge.Pair.LowerId);
        Assert.Equal(list[1].Id, nudge.Pair.HigherId);

        await using var db = _db.CreateDbContext();
        var row = await db.LookalikePairs.SingleAsync();
        Assert.Equal(now, row.FirstSeenAt);
        Assert.Null(row.DismissedAt);
        Assert.Equal(list[0].Id, row.LowerProductId);
        Assert.Equal(list[1].Id, row.HigherProductId);
    }

    [Fact]
    public async Task The_mood_ages_from_the_first_time_it_was_seen_not_each_visit()
    {
        var list = await SeedList();
        var first = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _service.GetActiveAsync(list, first); // FirstSeenAt = Sep 1

        // Eight days later he's Nagging — and re-seeing the pair must NOT reset the clock to "now".
        var nudge = Assert.Single(await _service.GetActiveAsync(list, first.AddDays(8)));
        Assert.Equal(NudgeMood.Nagging, nudge.Mood);

        await using var db = _db.CreateDbContext();
        var row = await db.LookalikePairs.SingleAsync(); // still exactly one row — not re-inserted
        Assert.Equal(first, row.FirstSeenAt); // unchanged, so the mood can keep degrading
    }

    [Fact]
    public async Task A_dismissed_pair_produces_no_nudge()
    {
        var list = await SeedList();
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _service.GetActiveAsync(list, now);

        await _service.DismissAsync(list[0].Id, list[1].Id, now);

        Assert.Empty(await _service.GetActiveAsync(list, now.AddDays(1)));
    }

    [Fact]
    public async Task DismissAsync_canonicalises_the_ids_so_order_does_not_matter()
    {
        var list = await SeedList();
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _service.GetActiveAsync(list, now); // records the row as (lower, higher)

        // Dismiss passing the ids the OTHER way round — it must hit the same row, not add a second.
        await _service.DismissAsync(list[1].Id, list[0].Id, now);

        await using var db = _db.CreateDbContext();
        var row = Assert.Single(await db.LookalikePairs.ToListAsync());
        Assert.NotNull(row.DismissedAt);
    }

    [Fact]
    public async Task Dismissing_a_pair_that_was_never_recorded_records_it_already_dismissed()
    {
        var list = await SeedList();
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        // Dismiss with no prior GetActiveAsync — dismissed the instant it appeared.
        await _service.DismissAsync(list[0].Id, list[1].Id, now);

        await using var db = _db.CreateDbContext();
        var row = Assert.Single(await db.LookalikePairs.ToListAsync());
        Assert.Equal(now, row.DismissedAt);
        Assert.Equal(now, row.FirstSeenAt);
        Assert.Empty(await _service.GetActiveAsync(list, now.AddDays(1))); // and stays silent
    }

    [Fact]
    public async Task UndismissAsync_makes_it_nudge_again_from_the_original_first_seen()
    {
        var list = await SeedList();
        var seen = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _service.GetActiveAsync(list, seen);                 // FirstSeenAt = Sep 1
        await _service.DismissAsync(list[0].Id, list[1].Id, seen.AddDays(2));
        Assert.Empty(await _service.GetActiveAsync(list, seen.AddDays(3))); // muted

        await _service.UndismissAsync(list[0].Id, list[1].Id);

        // Nudges again, and the mood picks up from the ORIGINAL Sep 1 (ten days on → Nagging), not the undo.
        var nudge = Assert.Single(await _service.GetActiveAsync(list, seen.AddDays(10)));
        Assert.Equal(NudgeMood.Nagging, nudge.Mood);
    }

    [Fact]
    public async Task DismissedForProductAsync_returns_the_dismissed_pairs_touching_a_product()
    {
        var list = await SeedList();
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await _service.GetActiveAsync(list, now);

        Assert.Empty(await _service.DismissedForProductAsync(list[0].Id)); // active ≠ dismissed

        await _service.DismissAsync(list[0].Id, list[1].Id, now);

        Assert.Single(await _service.DismissedForProductAsync(list[0].Id));
        Assert.Single(await _service.DismissedForProductAsync(list[1].Id));
        Assert.Empty(await _service.DismissedForProductAsync(list[2].Id)); // the milk is in no pair
    }

    [Fact]
    public async Task No_lookalikes_writes_nothing_and_returns_empty()
    {
        await using (var seed = _db.CreateDbContext())
        {
            seed.Products.AddRange(new Product { Name = "Whole Milk" }, new Product { Name = "Orange Juice" });
            await seed.SaveChangesAsync();
        }
        await using var db = _db.CreateDbContext();
        var list = await db.Products.AsNoTracking().ToListAsync();

        Assert.Empty(await _service.GetActiveAsync(list, DateTimeOffset.Now));
        Assert.Empty(await db.LookalikePairs.ToListAsync()); // nothing to remember ⇒ no write-on-read
    }
}
