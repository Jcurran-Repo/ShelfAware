using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// Answers "is this pending receipt an exact duplicate of one already confirmed?" — the guard that
/// keeps a re-uploaded photo from silently double-recording (uploads have no file dedup, and Smart
/// confirm would otherwise commit a trusted dupe without a pause). A detected duplicate NEVER
/// auto-confirms, whatever the mode: it queues with a warning, and recording it anyway is a human
/// decision. Detection is deliberately strict — same date, same merchant, same line count, same
/// lines, same prices — because a false "duplicate!" on a genuine twin trip (two milk runs in one
/// day) only costs one review click, but a lax match would nag about receipts that merely rhyme.
///
/// The comparison cascades cheapest-first: ONE indexed query prefilters on date + merchant + line
/// count (almost always zero candidates — done), and only survivors get the line-multiset
/// comparison, which itself bails on the first mismatch.
/// </summary>
public sealed class ReceiptDuplicateDetector(IHouseholdDbFactory dbFactory)
{
    public sealed record Match(int ReceiptId, DateOnly? PurchasedAt, string? Merchant);

    public async Task<Match?> FindDuplicateAsync(int receiptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.Receipts.AsNoTracking().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        // Undated can't be dated-matched (and Smart queues it for that already); empty has nothing
        // to compare.
        if (receipt is null || receipt.PurchasedAt is null || receipt.Lines.Count == 0) return null;

        // The cheap prefilter, pushed into SQL: date + merchant + line count. Only CONFIRMED
        // receipts count — a queued twin is still just one recording waiting to happen.
        var lineCount = receipt.Lines.Count;
        var candidates = await db.Receipts.AsNoTracking()
            .Where(r => r.Id != receipt.Id
                && r.Status == ReceiptStatus.Confirmed
                && r.PurchasedAt == receipt.PurchasedAt
                && r.Merchant == receipt.Merchant
                && r.Lines.Count == lineCount)
            .Include(r => r.Lines)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return null;

        // Raw text first: it's what the model read off the image, and review edits never touch it —
        // so a re-upload of the same photo matches even after the original's names were corrected.
        // Normalized names second, for the rare re-extraction that renders a raw line differently.
        var byRaw = LineKeys(receipt.Lines, l => l.RawText.Trim());
        var byName = LineKeys(receipt.Lines, l => l.NormalizedName.Trim().ToLowerInvariant());
        foreach (var candidate in candidates)
        {
            if (SameMultiset(byRaw, LineKeys(candidate.Lines, l => l.RawText.Trim())) ||
                SameMultiset(byName, LineKeys(candidate.Lines, l => l.NormalizedName.Trim().ToLowerInvariant())))
            {
                return new Match(candidate.Id, candidate.PurchasedAt, candidate.Merchant);
            }
        }
        return null;
    }

    /// <summary>One line as a comparable identity: its text key plus the quantity and price paid —
    /// "same products, same prices". Nulls compare as themselves, so two unpriced lines match.</summary>
    private static List<(string Key, decimal Quantity, decimal? UnitPrice)> LineKeys(
        IEnumerable<ReceiptLine> lines, Func<ReceiptLine, string> key) =>
        lines.Select(l => (key(l), l.Quantity, l.UnitPrice))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ThenBy(t => t.Quantity).ThenBy(t => t.UnitPrice)
            .ToList();

    private static bool SameMultiset(
        List<(string Key, decimal Quantity, decimal? UnitPrice)> a,
        List<(string Key, decimal Quantity, decimal? UnitPrice)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false; // both sorted — first difference decides
        }
        return true;
    }
}
