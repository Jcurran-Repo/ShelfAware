using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Web.Data;

/// <summary>One active PAIR nudge for the grocery list: the lookalike pair, and Eggs's mood for it (how long
/// it's lingered). A dismissed pair produces no nudge.</summary>
public sealed record LookalikeNudge(SimilarPair Pair, NudgeMood Mood);

/// <summary>One active CLUSTER nudge for the grocery list: three or more products sharing a head word, and
/// Eggs's mood for the cluster. A dismissed cluster produces no nudge.</summary>
public sealed record ClusterNudge(SimilarCluster Cluster, NudgeMood Mood);

/// <summary>Everything Eggs wants to say on the grocery list right now — pair nudges and cluster nudges. The
/// page ranks them together (most-bothered first) under its shared cap.</summary>
public sealed record ActiveNudges(IReadOnlyList<LookalikeNudge> Pairs, IReadOnlyList<ClusterNudge> Clusters)
{
    public static readonly ActiveNudges None = new([], []);
    public int Count => Pairs.Count + Clusters.Count;
}

/// <summary>A dismissed lookalike as seen from ONE product's detail page: the OTHER product in the pair,
/// by current id and name, so the page can offer to un-dismiss it.</summary>
public sealed record DismissedLookalike(int OtherProductId, string OtherName);

/// <summary>A dismissed lookalike CLUSTER as seen from ONE of its member products' detail page: the cluster's
/// head word and its OTHER current members, so the page can name them and offer to un-dismiss the cluster.</summary>
public sealed record DismissedCluster(string Head, IReadOnlyList<ClusterMember> Others);

/// <summary>Ties the pure detector (<see cref="SimilarPairs"/>) to Eggs's memory — per PAIR
/// (<see cref="LookalikePair"/>) and per CLUSTER (<see cref="LookalikeCluster"/>): remembers when he first
/// flagged each (so his mood can degrade), honours a permanent "they're different" dismissal, and reverses
/// one. The ONE place either memory is read and written, so the grocery list and a product's detail page
/// can't disagree about a pair's or a cluster's state.</summary>
public sealed class LookalikeNudgeService(
    IHouseholdDbFactory dbFactory, ILogger<LookalikeNudgeService> logger)
{
    /// <summary>The active nudges for the products currently on the shopping list: scan for the lookalike
    /// pairs and clusters, ensure each has a memory row (recording its first-seen the first time it's seen,
    /// so the mood can age), drop the dismissed, and return the rest with Eggs's mood.
    /// <paramref name="now"/> is passed in so the mood is testable.</summary>
    public async Task<ActiveNudges> GetActiveAsync(
        IReadOnlyList<Product> onList, DateTimeOffset now, CancellationToken ct = default)
    {
        var scan = SimilarPairs.Scan(onList);
        if (scan.Pairs.Count == 0 && scan.Clusters.Count == 0) return ActiveNudges.None;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Pairs: rows touching any involved product (a superset; the dictionary picks the exact ones).
        var involved = scan.Pairs.SelectMany(p => new[] { p.LowerId, p.HigherId }).Distinct().ToList();
        var pairRows = involved.Count == 0 ? [] : await db.LookalikePairs
            .Where(r => involved.Contains(r.LowerProductId) || involved.Contains(r.HigherProductId))
            .ToListAsync(ct);
        var pairByKey = pairRows.ToDictionary(r => (r.LowerProductId, r.HigherProductId));

        var newPairs = new List<LookalikePair>();
        var pairs = new List<LookalikeNudge>();
        foreach (var pair in scan.Pairs)
        {
            if (!pairByKey.TryGetValue((pair.LowerId, pair.HigherId), out var row))
            {
                row = new LookalikePair { LowerProductId = pair.LowerId, HigherProductId = pair.HigherId, FirstSeenAt = now };
                newPairs.Add(row);
            }
            if (row.DismissedAt is not null) continue; // "they're different" — permanent, so no nudge
            pairs.Add(new LookalikeNudge(pair, NudgeMoods.For(now - row.FirstSeenAt)));
        }

        // Clusters: one row per head word.
        var heads = scan.Clusters.Select(c => c.Head).ToList();
        var clusterRows = heads.Count == 0 ? [] : await db.LookalikeClusters
            .Where(r => heads.Contains(r.Head))
            .ToListAsync(ct);
        var clusterByHead = clusterRows.ToDictionary(r => r.Head, StringComparer.Ordinal);

        var newClusters = new List<LookalikeCluster>();
        var clusters = new List<ClusterNudge>();
        foreach (var cluster in scan.Clusters)
        {
            if (!clusterByHead.TryGetValue(cluster.Head, out var row))
            {
                row = new LookalikeCluster { Head = cluster.Head, FirstSeenAt = now };
                newClusters.Add(row);
            }
            if (row.DismissedAt is not null) continue; // "they're all different" — permanent, so no nudge
            clusters.Add(new ClusterNudge(cluster, NudgeMoods.For(now - row.FirstSeenAt)));
        }

        if (newPairs.Count > 0 || newClusters.Count > 0)
        {
            db.LookalikePairs.AddRange(newPairs);
            db.LookalikeClusters.AddRange(newClusters);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                // A concurrent list load (another tab) recorded one of these first, hitting a unique index.
                // Harmless: the active list computed above still stands (a just-seen nudge is Fresh either
                // way). Any new rows that DIDN'T collide roll back with the batch and are simply re-recorded on
                // the next load — no user-visible difference.
                logger.LogDebug(ex, "A lookalike memory row was recorded concurrently; using the state already computed.");
            }
        }
        return new ActiveNudges(pairs, clusters);
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

    /// <summary>Permanently mark a cluster "they're all different" — Eggs stops nudging about the products
    /// sharing that head word. Idempotent; a never-recorded head is recorded already-dismissed.</summary>
    public async Task DismissClusterAsync(string head, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LookalikeClusters.FirstOrDefaultAsync(r => r.Head == head, ct);
        if (row is null)
            db.LookalikeClusters.Add(new LookalikeCluster { Head = head, FirstSeenAt = now, DismissedAt = now });
        else if (row.DismissedAt is null)
            row.DismissedAt = now;
        else
            return; // already dismissed — nothing to write
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Undo a cluster dismissal (from a member product's detail page) — Eggs notices the cluster
    /// again, its mood resuming from the ORIGINAL first-seen, like a pair.</summary>
    public async Task UndismissClusterAsync(string head, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LookalikeClusters.FirstOrDefaultAsync(r => r.Head == head, ct);
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

    /// <summary>The dismissed cluster <paramref name="productId"/> belongs to, if any — for its detail page's
    /// "you told Eggs the yogurts are all different" (with an undo). Membership is asked of the detector over
    /// the tracked catalog, exactly as the grocery list asks it, so a cluster that no longer exists (members
    /// merged away until fewer than <see cref="SimilarPairs.ClusterSize"/> remain) drops out — there's nothing
    /// left to un-dismiss into. Carries the OTHER current members so the page can name them.</summary>
    public async Task<DismissedCluster?> DismissedClusterForProductAsync(int productId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var name = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        if (SimilarPairs.HeadOf(name) is not { } head) return null;

        var dismissed = await db.LookalikeClusters.AsNoTracking()
            .AnyAsync(r => r.Head == head && r.DismissedAt != null, ct);
        if (!dismissed) return null;

        // The cluster as the grocery list would see it right now: the tracked catalog, by name.
        var tracked = await db.Products.AsNoTracking().Where(p => p.IsTracked).OrderBy(p => p.Name).ToListAsync(ct);
        var cluster = SimilarPairs.Scan(tracked).Clusters.FirstOrDefault(c => c.Head == head);
        if (cluster is null) return null;

        return new DismissedCluster(head, [.. cluster.Members.Where(m => m.Id != productId)]);
    }

    private static (int Lower, int Higher) Canonical(int a, int b) => a < b ? (a, b) : (b, a);
}
