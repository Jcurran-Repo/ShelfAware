using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// <see cref="IReceiptImporter"/>: scans the inbox and auto-imports each new receipt file — extract →
/// AUTO-CONFIRM (no human review), reusing the same product-resolution + persistence as the manual
/// Upload confirm. Each file is its own transaction, so one bad receipt doesn't sink the batch.
/// </summary>
public class ReceiptImporter(
    IDbContextFactory<ShelfAwareDbContext> dbFactory,
    IReceiptExtractor extractor,
    IReceiptInbox inbox,
    AppPaths paths,
    ILogger<ReceiptImporter> logger) : IReceiptImporter
{
    public async Task<ImportSummary> ImportNewAsync(CancellationToken cancellationToken = default)
    {
        if (!await inbox.IsConfiguredAsync(cancellationToken)) return ImportSummary.NotConfigured;

        var items = await inbox.ListAsync(cancellationToken);
        HashSet<string> alreadyImported;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            alreadyImported = (await db.Receipts.Where(r => r.SourceFile != null)
                    .Select(r => r.SourceFile!).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var newItems = items.Where(i => !alreadyImported.Contains(i.Id)).ToList();
        if (newItems.Count == 0) return new ImportSummary(true, 0, 0, 0, 0);

        logger.LogInformation("Auto-import: {New} new receipt file(s) of {Total} in the inbox.", newItems.Count, items.Count);

        int imported = 0, purchases = 0, newProducts = 0, failed = 0;
        foreach (var item in newItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (p, np) = await ImportOneAsync(item, cancellationToken);
                imported++;
                purchases += p;
                newProducts += np;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Auto-import failed for receipt file {File}.", item.Name);
            }
        }

        logger.LogInformation("Auto-import done: {Imported} imported, {Purchases} purchase(s), {NewProducts} new product(s), {Failed} failed.",
            imported, purchases, newProducts, failed);
        return new ImportSummary(true, imported, purchases, newProducts, failed);
    }

    private async Task<(int Purchases, int NewProducts)> ImportOneAsync(InboxItem item, CancellationToken ct)
    {
        var bytes = await inbox.ReadAsync(item.Id, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var products = await db.Products.Include(p => p.Tags).OrderBy(p => p.Name).ToListAsync(ct);

        var extraction = await extractor.ExtractAsync(
            [new ReceiptAttachment(bytes, item.MediaType)],
            products.Select(p => p.Name).Distinct().ToList(),
            cancellationToken: ct);

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
            // Record it (pending review) so we don't re-extract a bad file every scan, then report failure.
            receipt.Status = ReceiptStatus.PendingReview;
            db.Receipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Extraction failed: {extraction.Error}");
        }

        receipt.Merchant = extraction.Receipt.Merchant;
        var purchaseDate = extraction.Receipt.PurchaseDate ?? DateOnly.FromDateTime(DateTime.Today);
        receipt.PurchasedAt = purchaseDate;
        receipt.Status = ReceiptStatus.Confirmed;

        var merchant = receipt.Merchant ?? "";
        // Load this merchant's aliases once (unique (Merchant, RawText) index); dedup within the receipt
        // so a repeated raw line doesn't try to insert a second alias.
        var aliasesByRaw = (await db.ProductAliases.Where(a => a.Merchant == merchant).ToListAsync(ct))
            .ToDictionary(a => a.RawText, a => a);
        var createdByName = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        int purchases = 0, newProducts = 0;

        foreach (var line in extraction.Receipt.Lines)
        {
            var name = line.NormalizedName.Trim();
            if (name.Length == 0) continue;
            var brand = string.IsNullOrWhiteSpace(line.Brand) ? null : line.Brand!.Trim();
            var size = string.IsNullOrWhiteSpace(line.Size) ? null : line.Size!.Trim();

            // Trust order (same as the review pre-fill): learned alias -> model suggestion -> matcher -> new.
            Product? resolved = null;
            if (aliasesByRaw.TryGetValue(line.RawText, out var alias))
                resolved = products.FirstOrDefault(p => p.Id == alias.ProductId);
            resolved ??= line.SuggestedProductName is { Length: > 0 }
                ? products.FirstOrDefault(p => string.Equals(p.Name, line.SuggestedProductName, StringComparison.OrdinalIgnoreCase))
                : null;
            resolved ??= ProductMatcher.Resolve(name, products);

            Product product;
            if (resolved is not null)
            {
                product = resolved;
            }
            else if (createdByName.TryGetValue(name, out var existingNew))
            {
                product = existingNew;
            }
            else
            {
                product = new Product { Name = name, Category = line.Category };
                db.Products.Add(product);
                products.Add(product); // so later lines in this receipt can match it too
                createdByName[name] = product;
                newProducts++;
            }

            db.PurchaseEvents.Add(new PurchaseEvent
            {
                Product = product,
                PurchasedAt = purchaseDate,
                Quantity = line.Quantity > 0 ? line.Quantity : 1,
                Brand = brand,
                Size = size,
                Source = PurchaseSource.Receipt,
            });
            purchases++;

            foreach (var rawTag in line.Tags)
            {
                var tag = rawTag.Trim();
                if (tag.Length == 0) continue;
                var existing = product.Tags.Select(t => t.Value).ToList();
                if (existing.Any(v => string.Equals(v, tag, StringComparison.OrdinalIgnoreCase))) continue;
                if (TagVocabulary.FindNearDuplicate(tag, existing) is not null) continue;
                product.Tags.Add(new ProductTag { Value = tag });
            }

            receipt.Lines.Add(new ReceiptLine
            {
                RawText = line.RawText,
                NormalizedName = name,
                Brand = brand,
                Size = size,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Category = line.Category,
                Confidence = line.Confidence,
                Product = product,
            });

            if (aliasesByRaw.TryGetValue(line.RawText, out var existingAlias))
            {
                existingAlias.Product = product;
            }
            else
            {
                var newAlias = new ProductAlias { Merchant = merchant, RawText = line.RawText, Product = product };
                db.ProductAliases.Add(newAlias);
                aliasesByRaw[line.RawText] = newAlias;
            }
        }

        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Auto-imported {File}: {Purchases} purchase(s), {NewProducts} new product(s).", item.Name, purchases, newProducts);
        return (purchases, newProducts);
    }
}
