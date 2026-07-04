using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// <see cref="IReceiptImporter"/>: scans the inbox and imports each new receipt file. Every receipt is
/// first persisted PENDING in exactly the shape a manual upload stores (lines + tags + the model's
/// product suggestion), then — depending on the <see cref="ImportMode"/> — either confirmed through the
/// same shared <see cref="ReceiptConfirmationService"/> the Upload review uses, or left queued for a
/// human. Each file is its own unit of work, so one bad receipt doesn't sink the batch.
/// </summary>
public class ReceiptImporter(
    IDbContextFactory<ShelfAwareDbContext> dbFactory,
    IReceiptExtractor extractor,
    IReceiptInbox inbox,
    IAppSettings settings,
    ReceiptConfirmationService confirmer,
    AppPaths paths,
    ILogger<ReceiptImporter> logger) : IReceiptImporter
{
    /// <summary>A line auto-confirms only at/above this extraction confidence (unless an alias vouches
    /// for it). Below it, Smart mode queues the receipt for human review.</summary>
    public const decimal SmartConfidenceFloor = 0.8m;

    // One scan at a time: the startup background scan, the Settings "Scan now" button, and the chat
    // import_receipts tool can otherwise interleave, each read the already-imported set before the
    // other saves, and import the same file twice.
    private static readonly SemaphoreSlim ScanLock = new(1, 1);

    public async Task<ImportSummary> ImportNewAsync(CancellationToken cancellationToken = default)
    {
        if (!await inbox.IsConfiguredAsync(cancellationToken)) return ImportSummary.NotConfigured;

        await ScanLock.WaitAsync(cancellationToken);
        try
        {
            return await ScanAsync(cancellationToken);
        }
        finally
        {
            ScanLock.Release();
        }
    }

    private async Task<ImportSummary> ScanAsync(CancellationToken cancellationToken)
    {
        var items = await inbox.ListAsync(cancellationToken);
        HashSet<string> alreadyImported;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            alreadyImported = (await db.Receipts.Where(r => r.SourceFile != null)
                    .Select(r => r.SourceFile!).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var newItems = items.Where(i => !alreadyImported.Contains(i.Id)).ToList();
        if (newItems.Count == 0) return new ImportSummary(true, 0, 0, 0, 0, 0);

        var mode = ImportModes.Parse(
            await settings.GetAsync(SettingKeys.ImportMode, cancellationToken),
            await settings.GetAsync(SettingKeys.AutoConfirmImports, cancellationToken));

        logger.LogInformation("Auto-import: {New} new receipt file(s) of {Total} ({Mode} mode).",
            newItems.Count, items.Count, mode);

        int imported = 0, purchases = 0, newProducts = 0, awaitingReview = 0, failed = 0;
        foreach (var item in newItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (p, np, queued) = await ImportOneAsync(item, mode, cancellationToken);
                if (queued) awaitingReview++;
                else { imported++; purchases += p; newProducts += np; }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Auto-import failed for receipt file {File}.", item.Name);
            }
        }

        logger.LogInformation("Auto-import done: {Imported} imported, {Queued} queued for review, {Purchases} purchase(s), {NewProducts} new product(s), {Failed} failed.",
            imported, awaitingReview, purchases, newProducts, failed);
        return new ImportSummary(true, imported, purchases, newProducts, awaitingReview, failed);
    }

    private async Task<(int Purchases, int NewProducts, bool Queued)> ImportOneAsync(
        InboxItem item, ImportMode mode, CancellationToken ct)
    {
        var bytes = await inbox.ReadAsync(item.Id, ct);

        List<Product> products;
        List<string> knownTags;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            products = await db.Products.OrderBy(p => p.Name).ToListAsync(ct);
            knownTags = TagVocabulary.Seed
                .Concat(await db.ProductTags.Select(t => t.Value).Distinct().ToListAsync(ct))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToList();
        }

        // Feed the live tag vocabulary too (dedup-at-source), exactly like the manual upload path —
        // skipping it here made the auto path coin near-duplicate tags the manual path wouldn't.
        var extraction = await extractor.ExtractAsync(
            [new ReceiptAttachment(bytes, item.MediaType)],
            products.Select(p => p.Name).Distinct().ToList(),
            knownTags,
            ct);

        // Keep a copy of the source file (mirrors the manual upload's audit trail).
        var folderName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40];
        Directory.CreateDirectory(Path.Combine(paths.ReceiptsDir, folderName));
        var ext = item.MediaType == "application/pdf" ? "pdf" : Path.GetExtension(item.Name).TrimStart('.');
        await File.WriteAllBytesAsync(Path.Combine(paths.ReceiptsDir, folderName, $"page-0.{ext}"), bytes, ct);

        var receipt = new Receipt
        {
            ImagePath = Path.Combine("receipts", folderName),
            RawModelJson = extraction.RawModelJson,
            SourceFile = item.Id, // marks this file imported so a re-scan skips it (even on failure)
        };

        if (!extraction.Success || extraction.Receipt is null)
        {
            // Record it so a re-scan doesn't re-extract the same file every startup. The Upload page
            // lists 0-line pending receipts under "couldn't be read" with Retry (re-extracts from the
            // saved copy) and Discard — a transient API failure isn't a silent permanent loss.
            receipt.Status = ReceiptStatus.PendingReview;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Extraction failed: {extraction.Error}");
        }

        receipt.Merchant = extraction.Receipt.Merchant;
        var purchaseDate = extraction.Receipt.PurchaseDate ?? DateOnly.FromDateTime(DateTime.Today);
        receipt.PurchasedAt = purchaseDate;
        receipt.Status = ReceiptStatus.PendingReview;

        // Persist the receipt + lines FIRST, pending, in the same shape a manual upload stores — so a
        // queued receipt loses nothing (tags + suggestion included) and confirm is the shared service.
        var merchant = receipt.Merchant ?? "";
        List<ProductAlias> aliases;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            aliases = await db.ProductAliases.Where(a => a.Merchant == merchant).ToListAsync(ct);
            foreach (var line in extraction.Receipt.Lines)
            {
                var name = line.NormalizedName.Trim();
                if (name.Length == 0) continue;
                receipt.Lines.Add(new ReceiptLine
                {
                    RawText = line.RawText,
                    NormalizedName = name,
                    Brand = string.IsNullOrWhiteSpace(line.Brand) ? null : line.Brand!.Trim(),
                    Size = string.IsNullOrWhiteSpace(line.Size) ? null : line.Size!.Trim(),
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Category = line.Category,
                    Confidence = line.Confidence,
                    TagsJson = ReceiptConfirmationService.SerializeTags(line.Tags),
                    SuggestedProduct = line.SuggestedProductName,
                });
            }
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync(ct);
        }

        // Resolve each line by the same trust order the review pre-fill uses:
        // learned alias → model suggestion → deterministic matcher → create new.
        var confirmLines = new List<ReceiptConfirmationService.ConfirmLine>();
        var allTrusted = true;
        foreach (var line in extraction.Receipt.Lines)
        {
            var name = line.NormalizedName.Trim();
            if (name.Length == 0) continue;

            var alias = aliases.FirstOrDefault(a => a.RawText == line.RawText);
            var resolved = alias is not null ? products.FirstOrDefault(p => p.Id == alias.ProductId) : null;
            resolved ??= line.SuggestedProductName is { Length: > 0 }
                ? products.FirstOrDefault(p => string.Equals(p.Name, line.SuggestedProductName, StringComparison.OrdinalIgnoreCase))
                : null;
            resolved ??= ProductMatcher.Resolve(name, products);

            // Trusted = a human-taught alias vouches for it, or it's a confident match to a product
            // that already exists. A brand-new product or a shaky line should get human eyes first.
            allTrusted &= alias is not null || (resolved is not null && line.Confidence >= SmartConfidenceFloor);

            confirmLines.Add(new ReceiptConfirmationService.ConfirmLine(
                line.RawText, name, line.Brand, line.Size, line.Quantity, line.Category, line.Tags,
                resolved?.Id ?? 0));
        }

        // Zero purchasable lines (e.g. not actually a receipt) always queues — confirming an empty
        // receipt would just hide it.
        var confirm = confirmLines.Count > 0 && mode switch
        {
            ImportMode.Auto => true,
            ImportMode.Smart => allTrusted,
            _ => false,
        };

        if (!confirm)
        {
            logger.LogInformation("Queued {File} for review: {Lines} line(s) ({Mode} mode).",
                item.Name, receipt.Lines.Count, mode);
            return (0, 0, true);
        }

        // writeAliases: false — no human looked at these pairings, so they must not become sticky
        // merchant aliases (a wrong machine match would silently pre-match every future receipt).
        var outcome = await confirmer.ConfirmAsync(receipt.Id, purchaseDate, confirmLines, writeAliases: false, ct);
        logger.LogInformation("Auto-imported {File}: {Purchases} purchase(s), {NewProducts} new product(s).",
            item.Name, outcome.Purchases, outcome.NewProducts);
        return (outcome.Purchases, outcome.NewProducts, false);
    }
}
