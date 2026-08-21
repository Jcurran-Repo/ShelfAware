using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>EF Core implementation of the chat data port (DESIGN.md §3/§7). Writes that a household can
/// undo also record an <c>ActivityEntry</c> through <see cref="IActivityLog"/> here in the data layer —
/// so a chat/voice action is logged for free, on the same one write path as the dashboard.</summary>
public class EfPantryStore(IHouseholdDbFactory dbFactory, IActivityLog activityLog) : IPantryStore
{
    public async Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Products
            .AsNoTracking() // read-only: the chat resolves/reads these; mutations use their own contexts
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Product?> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Same read-only shape as GetProductsAsync above, one row: the household query filter scopes the
        // lookup, so a foreign household's id comes back null exactly like an id that never existed.
        return await db.Products
            .AsNoTracking()
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
    }

    public async Task<int> CreateProductAsync(
        string name, Category category, IReadOnlyList<string> tags, string? defaultUnit = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = new Product
        {
            Name = name,
            Category = category,
            DefaultUnit = string.IsNullOrWhiteSpace(defaultUnit) ? null : defaultUnit.Trim(),
        };
        if (tags.Count > 0)
            TagVocabulary.ApplyTags(product, tags, await LoadVocabularyAsync(db, cancellationToken));
        db.Products.Add(product);

        // The product and its undo record commit together (see AddPurchaseAsync); the entry keys on the
        // product's generated id, so it saves inside the transaction to assign it, then stages the entry.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // assigns product.Id
        activityLog.Record(db, ActivityKind.ProductCreated, new ProductCreatedPayload(product.Id, product.Name));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return product.Id;
    }

    public async Task<IReadOnlyList<string>> AddTagsAsync(int productId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null) return [];

        var before = product.Tags.Select(t => t.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TagVocabulary.ApplyTags(product, tags, await LoadVocabularyAsync(db, cancellationToken));
        var added = product.Tags.Select(t => t.Value).Where(v => !before.Contains(v)).ToList();
        if (added.Count > 0)
        {
            activityLog.Record(db, ActivityKind.TagsAdded, new TagsAddedPayload(productId, product.Name, added));
            await db.SaveChangesAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
        }
        return added;
    }

    public async Task<IReadOnlyList<string>> GetKnownTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await LoadVocabularyAsync(db, cancellationToken)).OrderBy(t => t).ToList();
    }

    // The global tag universe (seed ∪ every stored tag) — the same vocabulary receipt confirmation
    // canonicalizes against, so chat-applied tags dedup identically.
    private static async Task<List<string>> LoadVocabularyAsync(ShelfAwareDbContext db, CancellationToken cancellationToken)
    {
        var stored = await db.ProductTags.Select(t => t.Value).Distinct().ToListAsync(cancellationToken);
        return TagVocabulary.Seed.Concat(stored).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PurchaseResult> AddPurchaseAsync(
        int productId, DateOnly purchasedAt, decimal quantity,
        PurchaseSource source = PurchaseSource.Chat, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The product must exist IN THIS HOUSEHOLD (the filtered lookup enforces it) — a raw id for
        // someone else's product must not become a cross-tenant insert; child rows aren't filtered
        // into existence, only queries are.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return new PurchaseResult(false, null);

        // Buying an item again ends its "don't want it for a while" (the grocery list's Untrack) —
        // resume predictions on every purchase path; receipts do the same in ReceiptConfirmationService.
        var retracked = false;
        if (!product.IsTracked)
        {
            product.IsTracked = true;
            retracked = true;
        }
        // §13.2: a purchase moves the count wherever it comes from — the dashboard, chat, or a receipt.
        StockLedger.Add(product, quantity);

        var purchase = new PurchaseEvent
        {
            ProductId = productId,
            PurchasedAt = purchasedAt,
            Quantity = quantity,
            Source = source,
        };
        db.PurchaseEvents.Add(purchase);

        // The buy and its undo record commit TOGETHER: a recorded purchase really happened, and a failed
        // buy leaves no orphan log row. The entry's payload needs the purchase's generated id, so inside
        // the transaction the buy saves first (assigning it), then the entry is staged, then one commit
        // covers both. PurchaseAddedHandler reverses the purchase, count and all.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // assigns purchase.Id
        var entry = activityLog.Record(db, ActivityKind.PurchaseAdded,
            new PurchaseAddedPayload(purchase.Id, quantity, product.Name, purchasedAt), source.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await activityLog.TrimAsync(cancellationToken); // retention: decoupled, best-effort, after the commit
        return new PurchaseResult(retracked, new ActivityRef(entry.Id, entry.Summary));
    }

    public async Task<ActivityRef?> RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Same in-household existence check as AddPurchaseAsync — no signals onto foreign products.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return null;

        var signal = new InventorySignal { ProductId = productId, Kind = kind, SignaledAt = DateTimeOffset.Now };
        db.InventorySignals.Add(signal);

        // The signal and its undo record commit together (see AddPurchaseAsync): the entry keys on the
        // signal's generated id, so the signal saves inside the transaction to assign it, then the entry.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // assigns signal.Id
        var entry = activityLog.Record(db, ActivityKind.SignalRecorded,
            new SignalRecordedPayload(signal.Id, kind, product.Name));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return new ActivityRef(entry.Id, entry.Summary);
    }

    public async Task<bool> SetPurchaseQuantityAsync(
        int purchaseId, decimal quantity, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0) return false;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Include the product: the correction has to move its count, and the filtered lookup is what
        // keeps a raw id from reaching another household's purchase.
        var purchase = await db.PurchaseEvents
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken);
        if (purchase?.Product is not { } product) return false;

        // The count moves by the DIFFERENCE, through the same ledger a confirm and a removal use — a
        // 12 corrected to 2 takes ten off the shelf. Not an attestation: the person is fixing what the
        // RECEIPT said, not reporting what they can see, so QuantityCountedAt stays where it was and
        // the staleness check keeps measuring from the last real look.
        var oldQuantity = purchase.Quantity;
        StockLedger.Add(product, quantity - purchase.Quantity);
        purchase.Quantity = quantity;

        // The correction and its undo record ride the one save (the entry keys on the existing purchase
        // id, so no transaction is needed). PurchaseQuantityEditedHandler restores the old quantity and
        // moves the count back by the same difference.
        if (quantity != oldQuantity)
            activityLog.Record(db, ActivityKind.PurchaseQuantityEdited,
                new PurchaseQuantityEditedPayload(purchaseId, oldQuantity, quantity, product.Name));

        // The receipt's own line is deliberately left alone: it's the audit copy of what was read, and
        // a PurchaseEvent points at a receipt rather than at a line, so a receipt with two lines for
        // one product couldn't be updated unambiguously anyway. /receipts stays a record of the
        // receipt; this page is the record of the pantry.
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetPurchaseBrandAsync(
        int purchaseId, string? brand, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The filtered lookup is the in-household existence rule — a raw id can't reach another
        // household's purchase. Include(Product) only for the undo entry's summary (brand moves no count).
        var purchase = await db.PurchaseEvents
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken);
        if (purchase is null) return false;

        // Blank folds to null — the same "unbranded" the Brands-bought grouping treats whitespace as,
        // so clearing a brand and never having one land in one state, not two.
        var oldBrand = purchase.Brand;
        purchase.Brand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim();
        if (purchase.Brand != oldBrand)
            activityLog.Record(db, ActivityKind.PurchaseBrandEdited,
                new PurchaseBrandEditedPayload(purchaseId, oldBrand, purchase.Brand, purchase.Product!.Name));
        // The receipt line is left alone, same as SetPurchaseQuantityAsync: the receipt is the record
        // of what was read, this page is the record of the pantry.
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetQuantityAsync(
        int productId, decimal quantity, bool relative = false, bool stopCounting = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Same in-household existence rule as every other mutation, enforced by the filtered lookup.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return false;

        // Snapshot the count-state before any mutation, so an undo can restore it exactly.
        var oldOnHand = product.QuantityOnHand;
        var oldCountedAt = product.QuantityCountedAt;
        var oldTrackQuantity = product.TrackQuantity;

        if (stopCounting)
        {
            StockLedger.StopCounting(product);
            if (product.TrackQuantity != oldTrackQuantity) // only a real change is worth an undo record
                RecordCountSet(db, product, oldOnHand, oldCountedAt, oldTrackQuantity, createdSignalId: null);
            await db.SaveChangesAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
            return true;
        }

        // An ABSOLUTE count below zero is refused, not clamped — the same rule as
        // SetPurchaseQuantityAsync, and for the same reason: "-5 on hand" is a number nobody means, and
        // quietly turning it into 0 would file an OutNow (§13.4) off a typo. A relative move is
        // different: "used two" against a count of one legitimately lands at none, and the ledger's
        // clamp is the right answer there.
        if (!relative && quantity < 0) return false;

        // A relative move is a human act but a statement about the DELTA, not the level — they saw
        // what they took, not the rows behind it — so it moves the number WITHOUT re-anchoring the
        // attestation clock (§13.1; landing at zero is the exception, stamped and asserted inside the
        // ledger). It needs a baseline to be relative TO; against an unknown count there is nothing to
        // subtract from, and a DORMANT count is frozen history a delta must not edit — both refusals
        // land on the same reply: state a fresh count to (re)start.
        if (relative && (!product.TrackQuantity || product.QuantityOnHand is null)) return false;

        var assertedOut = relative
            ? StockLedger.AdjustByHuman(product, quantity, DateTimeOffset.Now)
            : StockLedger.Attest(product, quantity, DateTimeOffset.Now);

        // Nothing actually moved (a zero-delta relative move) → nothing to record or undo.
        var changed = product.QuantityOnHand != oldOnHand
            || product.QuantityCountedAt != oldCountedAt
            || product.TrackQuantity != oldTrackQuantity;

        // §13.4: a HUMAN'S zero is real evidence, so it writes the outage the burn-rate rhythm learns
        // from — dated by running out rather than by remembering to report it. A zero that automated
        // decrements merely arrived at never reaches this method.
        if (assertedOut)
        {
            var signal = new InventorySignal
            {
                ProductId = productId,
                Kind = SignalKind.OutNow,
                SignaledAt = DateTimeOffset.Now,
            };
            db.InventorySignals.Add(signal);

            // The count change, the OutNow, and the undo record commit together; the entry keys on the
            // OutNow's generated id, so save inside the transaction to assign it, then stage the entry.
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken); // assigns signal.Id
            RecordCountSet(db, product, oldOnHand, oldCountedAt, oldTrackQuantity, signal.Id);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
            return true;
        }

        if (changed)
            RecordCountSet(db, product, oldOnHand, oldCountedAt, oldTrackQuantity, createdSignalId: null);
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    // The CountSet undo record from the pre-action snapshot plus the product's current (post-mutation)
    // count-state; createdSignalId is the OutNow this change filed, if any (an asserted zero).
    private void RecordCountSet(
        ShelfAwareDbContext db, Product product,
        decimal? oldOnHand, DateTimeOffset? oldCountedAt, bool oldTrackQuantity, int? createdSignalId) =>
        activityLog.Record(db, ActivityKind.CountSet, new CountSetPayload(
            product.Id, product.Name, oldOnHand, oldCountedAt, oldTrackQuantity,
            product.QuantityOnHand, product.TrackQuantity, createdSignalId));

    public async Task<bool> SetDefaultUnitAsync(int productId, string? unit, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Filtered lookup — the same in-household existence rule as every other mutation.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return false;

        var oldUnit = product.DefaultUnit;
        product.DefaultUnit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        if (product.DefaultUnit != oldUnit)
            activityLog.Record(db, ActivityKind.DefaultUnitSet,
                new DefaultUnitSetPayload(productId, oldUnit, product.DefaultUnit, product.Name));
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetExpirationAsync(int productId, DateOnly? expiresOn, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Same in-household existence rule as the other mutations, enforced by the filtered query. The
        // date lands on EVERY purchase from the latest buy date, not just the newest row: the engine
        // takes the longest date among that day's purchases, so a stale longer date on a same-day
        // sibling would silently outvote what the user just said ("the milk expires Friday" means the
        // milk they have, all of it).
        var latestBuy = await db.PurchaseEvents
            .Where(p => p.ProductId == productId)
            .OrderByDescending(p => p.PurchasedAt)
            .Select(p => (DateOnly?)p.PurchasedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestBuy is not { } day) return false;
        var stock = await db.PurchaseEvents
            .Where(p => p.ProductId == productId && p.PurchasedAt == day)
            .ToListAsync(cancellationToken);
        // Capture each affected row's old date BEFORE overwriting, so an undo can restore them.
        var rows = stock.Select(pp => new ExpirationRow(pp.Id, pp.ExpirationDate)).ToList();
        var changed = rows.Any(r => r.OldDate != expiresOn);
        foreach (var purchase in stock) purchase.ExpirationDate = expiresOn;
        if (changed)
        {
            var name = await db.Products.Where(pr => pr.Id == productId)
                .Select(pr => pr.Name).FirstOrDefaultAsync(cancellationToken) ?? "a product";
            activityLog.Record(db, ActivityKind.ExpirationSet,
                new ExpirationSetPayload(productId, name, expiresOn, rows));
        }
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    public async Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return;

        var was = product.IsTracked;
        product.IsTracked = tracked;
        if (was != tracked) // only a real change is worth an undo record
            activityLog.Record(db, ActivityKind.TrackingChanged,
                new TrackingChangedPayload(productId, was, tracked, product.Name));
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecipeRef>> GetRecipesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // DISPLAY order — the same order the Recipes page lists them (newest saved first, each original
        // followed by its adapted variants, also newest first) — so a positional reference the chat
        // resolves ("read the second recipe") lands on the recipe the user would count to on screen.
        var all = await db.Recipes
            .Select(r => new { r.Id, r.Name, HasSteps = r.Steps.Count > 0, r.SavedAt, r.ParentRecipeId })
            .ToListAsync(cancellationToken);
        return all
            .Where(r => r.ParentRecipeId is null)
            .OrderByDescending(r => r.SavedAt)
            .SelectMany(o => all
                .Where(v => v.ParentRecipeId == o.Id)
                .OrderByDescending(v => v.SavedAt)
                .Prepend(o))
            .Select(r => new RecipeRef(r.Id, r.Name, r.HasSteps))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> AddSubstitutesAsync(int productId, IReadOnlyList<string> values, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ProductSubstitutes
            .Where(s => s.ProductId == productId)
            .Select(s => s.Value)
            .ToListAsync(cancellationToken);
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var value in values)
        {
            var v = value.Trim();
            if (v.Length == 0 || !have.Add(v)) continue;
            db.ProductSubstitutes.Add(new ProductSubstitute { ProductId = productId, Value = v });
            added.Add(v);
        }
        if (added.Count > 0)
        {
            var name = await db.Products.Where(pr => pr.Id == productId)
                .Select(pr => pr.Name).FirstOrDefaultAsync(cancellationToken) ?? "a product";
            activityLog.Record(db, ActivityKind.SubstitutesAdded, new SubstitutesAddedPayload(productId, name, added));
            await db.SaveChangesAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
        }
        return added;
    }

    public async Task<IReadOnlyList<string>> GetExcludedFoodsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExcludedFoods.Select(f => f.Value).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> AddGroceryExtrasAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var have = new HashSet<string>(
            await db.GroceryExtras.Select(e => e.Name).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        foreach (var name in names)
        {
            var n = name.Trim();
            if (n.Length == 0 || !have.Add(n)) continue;
            db.GroceryExtras.Add(new GroceryExtra { Name = n });
            added.Add(n);
        }
        if (added.Count > 0)
        {
            activityLog.Record(db, ActivityKind.GroceryExtrasAdded, new GroceryExtrasAddedPayload(added));
            await db.SaveChangesAsync(cancellationToken);
            await activityLog.TrimAsync(cancellationToken);
        }
        return added;
    }

    public async Task<bool> RemoveGroceryExtraAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.GroceryExtras.FindAsync([id], cancellationToken);
        if (row is null) return false; // already gone (a double tap), or not this household's
        db.GroceryExtras.Remove(row);
        // Undoable: the entry matches by NAME (the row's id dies with it), so no generated-id dependency —
        // staged on this same save, so the removal and its undo record commit together.
        activityLog.Record(db, ActivityKind.GroceryExtraRemoved, new GroceryExtraRemovedPayload(row.Name));
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveSubstituteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ProductSubstitutes.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (row is null) return false;
        var productName = await db.Products.Where(pr => pr.Id == row.ProductId)
            .Select(pr => pr.Name).FirstOrDefaultAsync(cancellationToken) ?? "a product";
        db.ProductSubstitutes.Remove(row);
        activityLog.Record(db, ActivityKind.SubstituteRemoved,
            new SubstituteRemovedPayload(row.ProductId, productName, row.Value));
        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return true;
    }
}
