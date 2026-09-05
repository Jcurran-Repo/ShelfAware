using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Web.Data;

/// <summary>One active nudge for the grocery list: the lookalike pair, and Eggs's mood for it (how long
/// it's lingered). A dismissed pair produces no nudge.</summary>
public sealed record LookalikeNudge(SimilarPair Pair, NudgeMood Mood);

/// <summary>A dismissed lookalike as seen from ONE product's detail page: the OTHER product in the pair,
/// by current id and name, so the page can offer to un-dismiss it.</summary>
public sealed record DismissedLookalike(int OtherProductId, string OtherName);

/// <summary>Ties the pure detector (<see cref="SimilarPairs"/>) to Eggs's per-pair memory
/// (<see cref="LookalikePair"/>): remembers when he first flagged each pair (so his mood can degrade),
/// honours a permanent "they're different" dismissal, and reverses one. The ONE place the memory is read
/// and written, so the grocery list and a product's detail page can't disagree about a pair's state.</summary>
public sealed class LookalikeNudgeService(
    IHouseholdDbFactory dbFactory, ILogger<LookalikeNudgeService> logger)
{
    /// <summary>The active nudges for the products currently on the shopping list: detect the lookalike
    /// pairs, ensure each has a memory row (recording <see cref="LookalikePair.FirstSeenAt"/> the first time
    /// it's seen, so the mood can age), drop the dismissed, and return the rest with Eggs's mood.
    /// <paramref name="now"/> is passed in so the mood is testable.</summary>
    public async Task<IReadOnlyList<LookalikeNudge>> GetActiveAsync(
        IReadOnlyList<Product> onList, DateTimeOffset now, CancellationToken ct = default)
    {
        var detected = SimilarPairs.Find(onList);
        if (detected.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var involved = detected.SelectMany(p => new[] { p.LowerId, p.HigherId }).Distinct().ToList();
        var existing = await db.LookalikePairs
            .Where(r => involved.Contains(r.LowerProductId) || involved.Contains(r.HigherProductId))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(r => (r.LowerProductId, r.HigherProductId));

        var newRows = new List<LookalikePair>();
        var active = new List<LookalikeNudge>();
        foreach (var pair in detected)
        {
            if (!byKey.TryGetValue((pair.LowerId, pair.HigherId), out var row))
            {
                row = new LookalikePair { LowerProductId = pair.LowerId, HigherProductId = pair.HigherId, FirstSeenAt = now };
                newRows.Add(row);
            }
            if (row.DismissedAt is not null) continue; // "they're different" — permanent, so no nudge
            active.Add(new LookalikeNudge(pair, NudgeMoods.For(now - row.FirstSeenAt)));
        }

        if (newRows.Count > 0)
        {
            db.LookalikePairs.AddRange(newRows);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent list load (another tab) recorded one of these new pairs first, hitting the
                // unique (household, lower, higher) index. Harmless: the active list computed above still
                // stands (a just-seen pair is Fresh either way). Any new rows that DIDN'T collide roll back
                // with the batch and are simply re-recorded on the next load — no user-visible difference.
                logger.LogDebug(ex, "A lookalike pair was recorded concurrently; using the state already computed.");
            }
        }
        return active;
    }

    /// <summary>Permanently mark a pair "they're different" — Eggs stops nudging about it. Idempotent, and
    /// canonicalises the two ids itself, so a caller passes them in any order. If the pair was never
    /// recorded (dismissed the instant it appeared), it's recorded already-dismissed.</summary>
    public async Task DismissAsync(int productAId, int productBId, DateTimeOffset now, CancellationToken ct = default)
    {
        var (lo, hi) = Canonical(productAId, productBId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LookalikePairs.FirstOrDefaultAsync(r => r.LowerProductId == lo && r.HigherProductId == hi, ct);
        if (row is null)
            db.LookalikePairs.Add(new LookalikePair { LowerProductId = lo, HigherProductId = hi, FirstSeenAt = now, DismissedAt = now });
        else if (row.DismissedAt is null)
            row.DismissedAt = now;
        else
            return; // already dismissed — nothing to write
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Undo a dismissal (from a product's detail page) — Eggs notices the pair again. Its mood
    /// resumes from the ORIGINAL first-seen, deliberately: it never went away, you just muted it.</summary>
    public async Task UndismissAsync(int productAId, int productBId, CancellationToken ct = default)
    {
        var (lo, hi) = Canonical(productAId, productBId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LookalikePairs.FirstOrDefaultAsync(r => r.LowerProductId == lo && r.HigherProductId == hi, ct);
        if (row?.DismissedAt is null) return; // not there, or not dismissed — nothing to undo
        row.DismissedAt = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The dismissed pairs that involve <paramref name="productId"/> — for its detail page's "you
    /// told Eggs this and X are separate items" (each with an undo). Each carries the OTHER product's current
    /// name; a pair whose partner was since merged or deleted (the ids are breadcrumbs, not FKs) drops out —
    /// there's nothing left to un-dismiss into.</summary>
    public async Task<IReadOnlyList<DismissedLookalike>> DismissedForProductAsync(int productId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.LookalikePairs.AsNoTracking()
            .Where(r => r.DismissedAt != null && (r.LowerProductId == productId || r.HigherProductId == productId))
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var otherIds = rows.Select(r => r.LowerProductId == productId ? r.HigherProductId : r.LowerProductId).ToList();
        var names = await db.Products.AsNoTracking()
            .Where(p => otherIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        return [.. otherIds.Distinct()
            .Where(names.ContainsKey) // partner still exists
            .Select(id => new DismissedLookalike(id, names[id]))];
    }

    private static (int Lower, int Higher) Canonical(int a, int b) => a < b ? (a, b) : (b, a);
}
