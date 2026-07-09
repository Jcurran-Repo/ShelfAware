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
