using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>
/// The ONE path that turns a pending receipt into confirmed purchases — shared by the manual Upload
/// review and the auto-importer so the two can't drift (they were near-copies and had already diverged
/// on tag handling, quantity clamping, and alias policy). Idempotent: confirming an already-confirmed
/// receipt is a no-op, so a double-click or a queued duplicate event can't double-record purchases.
/// </summary>
public class ReceiptConfirmationService(IHouseholdDbFactory dbFactory, IActivityLog activityLog)
{
    /// <param name="ProductId">Resolved product id; 0 means "create a new product" from this line.</param>
    /// <param name="ExpirationDate">The label's expiration date the reviewer typed, or null. Defaulted so
    /// the auto-importer's machine confirms never carry one — the date is human-entered by definition.</param>
    public record ConfirmLine(
        string RawText, string NormalizedName, string? Brand, string? Size, string? Variety,
        decimal Quantity, Category Category, IReadOnlyList<string> Tags, int ProductId,
        DateOnly? ExpirationDate = null);

    /// <param name="Retracked">How many untracked products this receipt turned back on — buying an
    /// item again ends its "don't want it for a while" (the grocery list's Ignore-for-now untracks).</param>
    public record ConfirmOutcome(bool AlreadyConfirmed, int Purchases, int NewProducts, int Retracked = 0);

    /// <summary>
    /// Record the reviewed lines as purchases and mark the receipt confirmed — one SaveChanges, one
    /// transaction, so a failure persists nothing. <paramref name="writeAliases"/> is the trust
    /// boundary: aliases sit at the TOP of the match trust order on future receipts, so only
    /// human-confirmed pairings may write them. The auto-importer passes false — a wrong machine
    /// match must not become sticky and silently propagate to every later receipt.
    /// <paramref name="verifiedForEval"/> is the same trust boundary for accuracy ground truth:
    /// only the manual review path may pass true, and only when the user explicitly asserted they
    /// checked every line (see <see cref="Receipt.VerifiedForEval"/>).
    /// </summary>
    public async Task<ConfirmOutcome> ConfirmAsync(
        int receiptId, DateOnly purchaseDate, IReadOnlyList<ConfirmLine> lines,
        bool writeAliases, bool verifiedForEval = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.Receipts.Include(r => r.Lines)
                .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken)
            ?? throw new InvalidOperationException($"Receipt {receiptId} no longer exists.");
        if (receipt.Status == ReceiptStatus.Confirmed) return new(AlreadyConfirmed: true, 0, 0);

        // A purchase can't be in the future — a typo'd year would poison the cadence for weeks.
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (purchaseDate > today) purchaseDate = today;
        receipt.PurchasedAt = purchaseDate;

        var merchant = receipt.Merchant ?? "";
        var products = await db.Products.Include(p => p.Tags).ToListAsync(cancellationToken);

        // Global tag universe (seed ∪ every stored tag) so a new tag is canonicalized against ALL
        // existing tags, not just the one product's — keeps the tag cloud from fragmenting no matter
        // which path (manual or auto) confirmed the receipt.
        var vocabulary = TagVocabulary.Seed
            .Concat(products.SelectMany(p => p.Tags).Select(t => t.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Real receipts repeat identical raw lines and the (Merchant, RawText) alias index is unique —
        // track aliases per raw text (last write wins) and pair each confirmed line with a DISTINCT
        // stored line so duplicates each keep their own product link and price.
        var aliasesByRaw = writeAliases
            ? (await db.ProductAliases.Where(a => a.Merchant == merchant).ToListAsync(cancellationToken))
                .ToDictionary(a => a.RawText, a => a)
            : null;
        var unmatchedLines = receipt.Lines.ToList();
        // One trip can list a single NEW item on two lines — map both to one new product, keyed by
        // ProductMatcher's IDENTITY, not the raw name: the reader transcribes label text, so the two
        // lines are "Home-Canned Sauce" and "Home Canned Sauce" as often as they are character-identical,
        // and a raw key let that pair mint two products the matcher then calls one — on the app's
        // highest-volume creation path. Each line still records its own purchase.
        var createdByName = new Dictionary<string, Product>();
        var retracked = new HashSet<Product>(); // distinct — two lines of one item re-track it once
        int purchases = 0, created = 0;

        foreach (var line in lines)
        {
            var name = line.NormalizedName.Trim();
            if (name.Length == 0) continue;
            var brand = string.IsNullOrWhiteSpace(line.Brand) ? null : line.Brand!.Trim();
            var size = string.IsNullOrWhiteSpace(line.Size) ? null : line.Size!.Trim();
            var variety = string.IsNullOrWhiteSpace(line.Variety) ? null : line.Variety!.Trim();
            var quantity = line.Quantity > 0 ? line.Quantity : 1m;
            // The roll-up key: the matcher's identity — or, for a name with no identity content at all
            // ("!!" folds to an EMPTY key), the raw text itself, so two DIFFERENT junk names in one
            // receipt can't collide on "" and silently merge into one product. The two key kinds can't
            // cross: an identity key is letters/digits/spaces only, and a name whose key is empty
            // necessarily leads with a character no identity key contains.
            var identityKey = ProductMatcher.IdentityKey(name);
            var rollUpKey = identityKey.Length > 0 ? identityKey : name;

            Product product;
            if (line.ProductId > 0 && products.FirstOrDefault(p => p.Id == line.ProductId) is { } resolved)
            {
                product = resolved;
            }
            else if (createdByName.TryGetValue(rollUpKey, out var existingNew))
            {
                product = existingNew;
            }
            else
            {
                // CreatedByReceiptId is the provenance "remove this receipt" needs to know which
                // products the receipt introduced (vs merely bought again). Category comes from a circuit
                // <select> on the review grid, so IsDefined-guard it — a tampered message must not persist an
                // undefined enum (same guard SetCategory and create_product carry; default to Other).
                var category = Enum.IsDefined(line.Category) ? line.Category : Category.Other;
                product = new Product { Name = name, Category = category, CreatedByReceiptId = receipt.Id };
                db.Products.Add(product);
                products.Add(product); // later lines in this receipt can resolve to it
                createdByName[rollUpKey] = product;
                created++;
            }

            // Buying an item again ends its "don't want it for a while": the grocery list's
            // Ignore-for-now untracks a product, and a real purchase is the signal to resume
            // predictions. Applies on every confirm path (manual review and auto-import alike).
            if (!product.IsTracked)
            {
                product.IsTracked = true;
                retracked.Add(product);
            }

            db.PurchaseEvents.Add(new PurchaseEvent
            {
                Product = product,
                PurchasedAt = purchaseDate,
                Quantity = quantity,
                Brand = brand,
                Size = size,
                Variety = variety,
                ExpirationDate = line.ExpirationDate,
                Source = PurchaseSource.Receipt,
                ReceiptId = receipt.Id,
            });
            purchases++;

            // §13.2: the count moves by the quantity actually bought — three cans adds 3, never 1. A
            // no-op unless this product is counted AND has an established count; the rule lives in
            // StockLedger so removal can take back exactly this much.
            StockLedger.Add(product, quantity);

            TagVocabulary.ApplyTags(product, line.Tags, vocabulary);

            var dbLine = unmatchedLines.FirstOrDefault(l => l.RawText == line.RawText);
            if (dbLine is not null)
            {
                unmatchedLines.Remove(dbLine);
                dbLine.NormalizedName = name;
                dbLine.Brand = brand;
                dbLine.Size = size;
                dbLine.Variety = variety;
                dbLine.ExpirationDate = line.ExpirationDate;
                dbLine.Quantity = quantity;
                dbLine.Category = line.Category;
                dbLine.Product = product;
                dbLine.TagsJson = SerializeTags(line.Tags);
            }

            if (aliasesByRaw is not null)
            {
                if (aliasesByRaw.TryGetValue(line.RawText, out var alias))
                {
                    // Re-POINTING is new teaching (stamp the teacher); re-walking the same pairing
                    // is not — a duplicate confirm must not inherit credit for an earlier receipt's
                    // lesson, or removing the dupe would un-teach what the original taught. A product
                    // created THIS confirm has Id 0 here, which never equals a stored alias's real
                    // ProductId — so pointing an alias at a new product always counts as teaching.
                    if (alias.ProductId != product.Id)
                    {
                        alias.Product = product;
                        alias.TaughtByReceiptId = receipt.Id;
                    }
                }
                else
                {
                    var newAlias = new ProductAlias
                    {
                        Merchant = merchant, RawText = line.RawText, Product = product,
                        TaughtByReceiptId = receipt.Id,
                    };
                    db.ProductAliases.Add(newAlias);
                    aliasesByRaw[line.RawText] = newAlias;
                }
            }
        }

        receipt.Status = ReceiptStatus.Confirmed;
        // The confirm's own moment — what lets removal order this confirm against a later count
        // (§13.2). The early AlreadyConfirmed return above is what keeps a re-confirm from moving it.
        receipt.ConfirmedAt = DateTimeOffset.Now;
        receipt.VerifiedForEval = verifiedForEval;

        // The undo record, committed with the confirm (staged on this same context — the receipt id and
        // image path are already known, so no id-assigning first save is needed). Its undo is a total
        // removal (ReceiptRemovalService), reachable from /history whether this was a manual or an auto
        // confirm. Only when the confirm actually recorded purchases — a confirm that landed nothing has
        // nothing to unwind, and RemovalService would call it untraceable. The image path rides in the
        // payload so the post-commit image delete needs no DB read once the receipt row is gone.
        if (purchases > 0)
            activityLog.Record(db, ActivityKind.ReceiptConfirmed,
                new ReceiptConfirmedPayload(receipt.Id, receipt.Merchant, purchases, receipt.ImagePath));

        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken); // retention: best-effort, after the commit
        return new(AlreadyConfirmed: false, purchases, created, retracked.Count);
    }

    /// <summary>Tags ride on <see cref="ReceiptLine.TagsJson"/> as a JSON array (null when empty).</summary>
    public static string? SerializeTags(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? null : JsonSerializer.Serialize(tags);

    public static List<string> DeserializeTags(string? tagsJson)
    {
        if (string.IsNullOrEmpty(tagsJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? []; }
        catch (JsonException) { return []; }
    }
}
