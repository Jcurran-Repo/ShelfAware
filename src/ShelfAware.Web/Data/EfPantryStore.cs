using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Web.Data;

/// <summary>EF Core implementation of the chat data port (DESIGN.md §3/§7).</summary>
public class EfPantryStore(IHouseholdDbFactory dbFactory) : IPantryStore
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

    public async Task<int> CreateProductAsync(string name, Category category, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = new Product { Name = name, Category = category };
        if (tags.Count > 0)
            TagVocabulary.ApplyTags(product, tags, await LoadVocabularyAsync(db, cancellationToken));
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
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
        if (added.Count > 0) await db.SaveChangesAsync(cancellationToken);
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

    public async Task<bool> AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // The product must exist IN THIS HOUSEHOLD (the filtered lookup enforces it) — a raw id for
        // someone else's product must not become a cross-tenant insert; child rows aren't filtered
        // into existence, only queries are.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return false;

        // Buying an item again ends its "don't want it for a while" (the grocery list's Untrack) —
        // resume predictions on every purchase path; receipts do the same in ReceiptConfirmationService.
        var retracked = false;
        if (!product.IsTracked)
        {
            product.IsTracked = true;
            retracked = true;
        }
        // §13.2: a purchase moves the count wherever it comes from. Chat purchases carry no ReceiptId,
        // so unlike a receipt's this one has no undo — which is fine, it's a deliberate human statement
        // rather than a machine reading, and the count is correctable by hand.
        StockLedger.Add(product, quantity);

        db.PurchaseEvents.Add(new PurchaseEvent
        {
            ProductId = productId,
            PurchasedAt = purchasedAt,
            Quantity = quantity,
            Source = PurchaseSource.Chat,
        });
        await db.SaveChangesAsync(cancellationToken);
        return retracked;
    }

    public async Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Same in-household existence check as AddPurchaseAsync — no signals onto foreign products.
        if (await db.Products.FindAsync([productId], cancellationToken) is null) return;
        db.InventorySignals.Add(new InventorySignal
        {
            ProductId = productId,
            Kind = kind,
            SignaledAt = DateTimeOffset.Now,
        });
        await db.SaveChangesAsync(cancellationToken);
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
        StockLedger.Add(product, quantity - purchase.Quantity);
        purchase.Quantity = quantity;

        // The receipt's own line is deliberately left alone: it's the audit copy of what was read, and
        // a PurchaseEvent points at a receipt rather than at a line, so a receipt with two lines for
        // one product couldn't be updated unambiguously anyway. /receipts stays a record of the
        // receipt; this page is the record of the pantry.
        await db.SaveChangesAsync(cancellationToken);
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

        if (stopCounting)
        {
            StockLedger.StopCounting(product);
            await db.SaveChangesAsync(cancellationToken);
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
        // subtract from, and inventing one is the error §13.2 exists to avoid.
        if (relative && product.QuantityOnHand is null) return false;

        var assertedOut = relative
            ? StockLedger.AdjustByHuman(product, quantity, DateTimeOffset.Now)
            : StockLedger.Attest(product, quantity, DateTimeOffset.Now);

        // §13.4: a HUMAN'S zero is real evidence, so it writes the outage the burn-rate rhythm learns
        // from — dated by running out rather than by remembering to report it. A zero that automated
        // decrements merely arrived at never reaches this method.
        if (assertedOut)
        {
            db.InventorySignals.Add(new InventorySignal
            {
                ProductId = productId,
                Kind = SignalKind.OutNow,
                SignaledAt = DateTimeOffset.Now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetDefaultUnitAsync(int productId, string? unit, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Filtered lookup — the same in-household existence rule as every other mutation.
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return false;

        product.DefaultUnit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        await db.SaveChangesAsync(cancellationToken);
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
        foreach (var purchase in stock) purchase.ExpirationDate = expiresOn;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FindAsync([productId], cancellationToken);
        if (product is null) return;
        product.IsTracked = tracked;
        await db.SaveChangesAsync(cancellationToken);
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
        if (added.Count > 0) await db.SaveChangesAsync(cancellationToken);
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
        if (added.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
