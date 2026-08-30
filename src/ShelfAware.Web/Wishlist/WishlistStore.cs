using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Wishlist;

/// <summary>Reads and writes the wishlist over auth.db, kept out of any page so the count, email dedup
/// and retention rules are directly testable. Mirrors <see cref="ShelfAware.Web.Diagnostics.ErrorLogStore"/>:
/// operator data, admin-only reads. Writes arrive from the PUBLIC /about form — a public write path,
/// hardened at the edge (per-IP rate limit + honeypot + validation on the page); here it just records a
/// row it trusts the caller to have validated (the tier key especially).</summary>
public sealed class WishlistStore(IDbContextFactory<AuthDbContext> dbFactory)
{
    /// <summary>A generous bound so a public endpoint can't grow the table forever (the rate limit makes
    /// reaching it hard). The trim sheds the oldest EMAIL-LESS rows first, so abuse loses anonymous
    /// clicks before it ever costs a real notify address — the emails are the point of the list.</summary>
    public const int MaxRows = 5000;

    public async Task RecordAsync(string tier, string? email, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Wishlist.Add(new WishlistEntry
        {
            Tier = tier,
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            CreatedAt = at,
        });
        await db.SaveChangesAsync(ct);
        await TrimAsync(db, ct);
    }

    // SQLite refuses DateTimeOffset in a SQL ORDER BY (see ErrorLogStore), so the trim's pick happens
    // client-side — cheap because the table is bounded.
    private static async Task TrimAsync(AuthDbContext db, CancellationToken ct)
    {
        var over = await db.Wishlist.CountAsync(ct) - MaxRows;
        if (over <= 0) return;
        var rows = await db.Wishlist.Select(r => new TrimRow(r.Id, r.Email, r.CreatedAt)).ToListAsync(ct);
        var doomed = DoomedIds(rows, over);
        await db.Wishlist.Where(r => doomed.Contains(r.Id)).ExecuteDeleteAsync(ct);
    }

    internal sealed record TrimRow(int Id, string? Email, DateTimeOffset CreatedAt);

    /// <summary>Which rows the trim sheds when <paramref name="over"/> the cap: anonymous (email-less)
    /// rows first, oldest within each group — so a notify address always outlives a bare interest click,
    /// which is the point of collecting emails. Pure, so the ordering is directly testable without
    /// inserting <see cref="MaxRows"/>+1 rows.</summary>
    internal static List<int> DoomedIds(IEnumerable<TrimRow> rows, int over) =>
        [.. rows
            .OrderBy(r => string.IsNullOrEmpty(r.Email) ? 0 : 1) // anonymous first
            .ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Take(over).Select(r => r.Id)];

    /// <summary>The public interest count for the page — total submissions. Soft by design (a click
    /// costs nothing); the trusted number is <see cref="WishlistSummary.DistinctEmails"/>.</summary>
    public async Task<int> InterestCountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Wishlist.CountAsync(ct);
    }

    /// <summary>The admin overview: soft total, the trusted distinct-email count, and the tier
    /// breakdown. Email dedup is case-insensitive and computed at READ time (no unique constraint) so a
    /// person who reserves twice — or changes their intended tier — never fails a write.</summary>
    public async Task<WishlistSummary> SummarizeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Wishlist.AsNoTracking().ToListAsync(ct);
        var distinctEmails = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Email))
            .Select(r => r.Email!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var byTier = rows.GroupBy(r => r.Tier).ToDictionary(g => g.Key, g => g.Count());
        return new WishlistSummary(rows.Count, distinctEmails, byTier);
    }

    /// <summary>The notify list for the admin export — one row per distinct email (case-insensitive),
    /// carrying its latest chosen tier, newest first. This is what you mail when hosting is ready.</summary>
    public async Task<List<WishlistContact>> ContactsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Wishlist.AsNoTracking()
            .Where(r => r.Email != null && r.Email != "").ToListAsync(ct);
        return [.. rows
            .GroupBy(r => r.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
            .Select(r => new WishlistContact(r.Email!.Trim(), r.Tier, r.CreatedAt))
            .OrderByDescending(c => c.SignedUpAt)];
    }
}

/// <summary>The admin overview numbers. <paramref name="Total"/> is soft (raw submissions);
/// <paramref name="DistinctEmails"/> is the signal worth acting on.</summary>
public sealed record WishlistSummary(int Total, int DistinctEmails, IReadOnlyDictionary<string, int> ByTier);

public sealed record WishlistContact(string Email, string Tier, DateTimeOffset SignedUpAt);
