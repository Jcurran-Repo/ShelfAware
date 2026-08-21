using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// The one path from receipt IMAGES to a persisted <see cref="ReceiptStatus.PendingReview"/> receipt,
/// plus the graduated-trust auto-confirm routing that may record it on the spot. Lifted out of
/// <c>Upload.razor</c> so the page and the <c>POST /api/receipts/extract</c> endpoint share EXACTLY one
/// definition of "ingest a receipt" — the review/confirm steps that follow stay on the page. One call
/// covers ONE receipt, which may span several page images (the "these are one long receipt" case); a
/// batch of separate receipts is several calls.
/// <para>Extraction and persistence live together here on purpose. <see cref="RetryAsync"/> re-reads a
/// failed receipt from its saved audit copy, and it shares the same catalog load, the same
/// <c>ExtractedReceipt</c> → <see cref="ReceiptLine"/> mapping, and the same persistence rules as a
/// fresh ingest — two copies of those would be two places to fix a mapping bug.</para>
/// </summary>
public sealed class ReceiptIngestionService(
    IHouseholdDbFactory dbFactory,
    IReceiptExtractor extractor,
    ReceiptStorage storage,
    ReceiptAutoConfirmer autoConfirmer,
    ILogger<ReceiptIngestionService> logger)
{
    /// <summary>The result of ingesting one receipt. On a failed READ, <see cref="Success"/> is false but
    /// the receipt is still persisted (audit copy + raw model output) so the review queue's Retry can
    /// re-extract, and <see cref="ReceiptId"/> points at it. On a successful read, the auto-confirm fields
    /// report whether the household's <c>ImportMode</c> recorded it without review.</summary>
    public sealed record IngestOutcome(
        int ReceiptId,
        bool Success,
        string? Error,
        string? Merchant,
        DateOnly? PurchasedAt,
        int LineCount,
        bool AutoConfirmed,
        int Purchases,
        int NewProducts,
        int Retracked,
        bool PossibleDuplicate);

    /// <summary>Extract one receipt from its page image(s), persist it as PendingReview, then let the
    /// auto-confirmer decide whether to record it now or leave it for review. The audit copy is written
    /// BEFORE extraction, so a read that fails still leaves a receipt the queue's Retry can re-run.</summary>
    public async Task<IngestOutcome> IngestAsync(
        IReadOnlyList<ReceiptAttachment> pages, CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
            throw new ArgumentException("A receipt needs at least one page image.", nameof(pages));

        var imagePath = await storage.NewFolderAsync(cancellationToken);
        var index = 0;
        foreach (var page in pages)
            await storage.WritePageAsync(imagePath, index++, page.Data, page.MediaType, cancellationToken);

        var (productNames, tags) = await LoadCatalogAsync(cancellationToken);
        var result = await extractor.ExtractAsync(pages, productNames, tags, cancellationToken);
        var receipt = await PersistExtractionAsync(imagePath, result, cancellationToken);

        if (!result.Success || result.Receipt is null)
            return new IngestOutcome(receipt.Id, Success: false, result.Error, receipt.Merchant,
                receipt.PurchasedAt, receipt.Lines.Count, AutoConfirmed: false, 0, 0, 0, PossibleDuplicate: false);

        // Smart/Auto route: a trusted receipt records itself; anything the router won't vouch for falls
        // through to review. A router failure is non-fatal — the receipt is safely pending and review is
        // the honest recovery — so it degrades to "queued" rather than sinking a read that actually worked
        // (Upload.razor's own rule, kept here).
        ReceiptAutoConfirmer.Outcome auto;
        try
        {
            auto = await autoConfirmer.TryConfirmAsync(receipt.Id, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-confirm routing failed for receipt {ReceiptId}; falling back to review.", receipt.Id);
            auto = ReceiptAutoConfirmer.Outcome.Queued;
        }

        return new IngestOutcome(receipt.Id, Success: true, Error: null, receipt.Merchant, receipt.PurchasedAt,
            receipt.Lines.Count, auto.Confirmed, auto.Purchases, auto.NewProducts, auto.Retracked,
            PossibleDuplicate: auto.Duplicate is not null);
    }

    /// <summary>Why a <see cref="RetryAsync"/> couldn't finish, so the caller can word each case for itself.</summary>
    public enum RetryFailure { None, Gone, MissingCopy, ReadFailed }

    /// <summary>The outcome of re-extracting a queued receipt from its saved copy.</summary>
    public sealed record RetryResult(bool Ok, RetryFailure Failure, string? Error)
    {
        public static readonly RetryResult Succeeded = new(true, RetryFailure.None, null);
        public static readonly RetryResult Gone = new(false, RetryFailure.Gone, null);
        public static readonly RetryResult MissingCopy = new(false, RetryFailure.MissingCopy, null);
        public static RetryResult ReadFailed(string? error) => new(false, RetryFailure.ReadFailed, error);
    }

    /// <summary>Re-extract a failed (usually zero-line) receipt from the audit copy saved at ingest time,
    /// updating it in place and dropping its old lines — a transient API blip during extraction used to
    /// park the file with no way back but the server logs. Straight into the normal review flow on success;
    /// deliberately does NOT auto-confirm route (a receipt that already needed a hand goes to a human).</summary>
    public async Task<RetryResult> RetryAsync(int receiptId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var r = await db.Receipts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);
        if (r is null) return RetryResult.Gone;

        var files = storage.Pages(r.ImagePath);
        if (files.Count == 0) return RetryResult.MissingCopy;

        var attachments = new List<ReceiptAttachment>();
        foreach (var file in files)
        {
            var (bytes, mediaType) = await storage.ReadPageAsync(file, cancellationToken);
            attachments.Add(new ReceiptAttachment(bytes, mediaType));
        }

        var (productNames, tags) = await LoadCatalogAsync(cancellationToken);
        var result = await extractor.ExtractAsync(attachments, productNames, tags, cancellationToken);
        if (!result.Success || result.Receipt is null) return RetryResult.ReadFailed(result.Error);

        r.RawModelJson = result.RawModelJson;
        ApplyHeader(r, result.Receipt);
        db.ReceiptLines.RemoveRange(r.Lines);
        r.Lines = MapLines(result.Receipt);
        await db.SaveChangesAsync(cancellationToken);
        return RetryResult.Succeeded;
    }

    // Products + tag vocabulary for LLM-assisted matching and tag reuse during extraction. Names only
    // (the extractor never needs the whole entity) and untracked — this is a read that feeds a prompt.
    private async Task<(List<string> ProductNames, List<string> Tags)> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var productNames = await db.Products.AsNoTracking()
            .OrderBy(p => p.Name).Select(p => p.Name).Distinct().ToListAsync(cancellationToken);
        var stored = await db.ProductTags.AsNoTracking().Select(t => t.Value).Distinct().ToListAsync(cancellationToken);
        var tags = TagVocabulary.Seed.Concat(stored)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        return (productNames, tags);
    }

    /// <summary>Persist ANY extraction outcome as a PendingReview receipt — a successful read keeps its
    /// lines; a failed one keeps the audit copy + raw model output so the queue's Retry can re-extract.</summary>
    private async Task<Receipt> PersistExtractionAsync(string imagePath, ExtractionResult result, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var r = new Receipt
        {
            ImagePath = imagePath,
            RawModelJson = result.RawModelJson,
            Status = ReceiptStatus.PendingReview,
        };
        if (result.Success && result.Receipt is not null)
        {
            ApplyHeader(r, result.Receipt);
            r.Lines = MapLines(result.Receipt);
        }
        db.Receipts.Add(r);
        await db.SaveChangesAsync(cancellationToken);
        return r;
    }

    /// <summary>Copy the receipt-level fields extraction reads off the paper — merchant, date, and the
    /// printed money totals — onto the entity. One definition so a fresh ingest and a Retry re-extract
    /// can't drift on which header fields they carry over (the line mapping differs; this doesn't).</summary>
    private static void ApplyHeader(Receipt r, ExtractedReceipt extracted)
    {
        r.Merchant = extracted.Merchant;
        r.PurchasedAt = extracted.PurchaseDate;
        r.Subtotal = extracted.Subtotal;
        r.Tax = extracted.Tax;
        r.Total = extracted.Total;
        r.Savings = extracted.Savings;
    }

    private static List<ReceiptLine> MapLines(ExtractedReceipt extracted) => extracted.Lines.Select(l => new ReceiptLine
    {
        RawText = l.RawText,
        NormalizedName = l.NormalizedName,
        Brand = l.Brand,
        Size = l.Size,
        Variety = l.Variety,
        Quantity = l.Quantity,
        UnitPrice = l.UnitPrice,
        Category = l.Category,
        Confidence = l.Confidence,
        TagsJson = ReceiptConfirmationService.SerializeTags(l.Tags),
        SuggestedProduct = l.SuggestedProductName,
    }).ToList();
}
