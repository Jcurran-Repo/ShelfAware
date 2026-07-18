using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class EfPantryStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EfPantryStore _store;

    public EfPantryStoreTests() => _store = new EfPantryStore(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Adding_a_purchase_retracks_an_ignored_product_and_reports_it()
    {
        // The chat/voice "bought X" path must end "don't want it for a while" (grocery-list Untrack)
        // the same way a receipt confirm does, and return true so the assistant can say so.
        int cocoaId;
        await using (var db = _db.CreateDbContext())
        {
            var cocoa = new Product { Name = "Cocoa Powder", IsTracked = false };
            db.Products.Add(cocoa);
            await db.SaveChangesAsync();
            cocoaId = cocoa.Id;
        }

        var retracked = await _store.AddPurchaseAsync(cocoaId, new DateOnly(2026, 7, 6), 1m);

        Assert.True(retracked);
        await using var read = _db.CreateDbContext();
        Assert.True((await read.Products.SingleAsync(p => p.Id == cocoaId)).IsTracked);
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(pe => pe.ProductId == cocoaId));
    }

    [Fact]
    public async Task Set_expiration_lands_on_every_latest_day_purchase_and_leaves_history_alone()
    {
        // The engine takes the LONGEST date among the latest day's purchases, so the date must land on
        // all of them — a stale longer date on a same-day sibling would silently outvote the user.
        int milkId;
        await using (var db = _db.CreateDbContext())
        {
            var milk = new Product { Name = "Whole Milk" };
            db.Products.Add(milk);
            db.PurchaseEvents.Add(new PurchaseEvent { Product = milk, PurchasedAt = new DateOnly(2026, 7, 1), ExpirationDate = new DateOnly(2026, 7, 9) });
            db.PurchaseEvents.Add(new PurchaseEvent { Product = milk, PurchasedAt = new DateOnly(2026, 7, 10) });
            db.PurchaseEvents.Add(new PurchaseEvent { Product = milk, PurchasedAt = new DateOnly(2026, 7, 10) });
            await db.SaveChangesAsync();
            milkId = milk.Id;
        }

        var ok = await _store.SetExpirationAsync(milkId, new DateOnly(2026, 7, 17));

        Assert.True(ok);
        await using var read = _db.CreateDbContext();
        var purchases = await read.PurchaseEvents.Where(p => p.ProductId == milkId).ToListAsync();
        Assert.All(purchases.Where(p => p.PurchasedAt == new DateOnly(2026, 7, 10)),
            p => Assert.Equal(new DateOnly(2026, 7, 17), p.ExpirationDate));
        // The 7/1 purchase keeps ITS history untouched — old jugs' dates are a record, not stock state.
        Assert.Equal(new DateOnly(2026, 7, 9),
            purchases.Single(p => p.PurchasedAt == new DateOnly(2026, 7, 1)).ExpirationDate);

        // And null clears the same rows.
        Assert.True(await _store.SetExpirationAsync(milkId, null));
        await using var read2 = _db.CreateDbContext();
        Assert.All(await read2.PurchaseEvents.Where(p => p.ProductId == milkId && p.PurchasedAt == new DateOnly(2026, 7, 10)).ToListAsync(),
            p => Assert.Null(p.ExpirationDate));
    }

    [Fact]
    public async Task Set_expiration_reports_false_when_there_is_no_purchase_to_carry_it()
    {
        var id = await _store.CreateProductAsync("Brand New Thing", Category.Pantry, []);

        Assert.False(await _store.SetExpirationAsync(id, new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public async Task Creating_with_tags_canonicalizes_against_the_vocabulary()
    {
        // Chat-applied tags go through the same dedup as receipt confirmation: "proteins" is a
        // near-duplicate of the seed tag "Protein" and must map to it, not fragment the cloud.
        var id = await _store.CreateProductAsync("Wagyu Beef Tips", Category.Meat, ["proteins", "Beef"]);

        await using var read = _db.CreateDbContext();
        var tags = (await read.Products.Include(p => p.Tags).SingleAsync(p => p.Id == id))
            .Tags.Select(t => t.Value).ToList();
        Assert.Contains("Protein", tags);
        Assert.Contains("Beef", tags); // genuinely new — kept as coined
        Assert.DoesNotContain("proteins", tags);
    }

    [Fact]
    public async Task Adding_tags_skips_duplicates_and_reports_what_was_added()
    {
        var id = await _store.CreateProductAsync("Wagyu Beef Tips", Category.Meat, ["Beef"]);

        var added = await _store.AddTagsAsync(id, ["beef", "Steak"]); // "beef" already there as "Beef"

        Assert.Equal(new[] { "Steak" }, added);
        await using var read = _db.CreateDbContext();
        var tags = (await read.Products.Include(p => p.Tags).SingleAsync(p => p.Id == id))
            .Tags.Select(t => t.Value).ToList();
        Assert.Equal(2, tags.Count);
    }

    [Fact]
    public async Task Recipes_list_in_page_display_order_so_positional_references_land_right()
    {
        // "Read the second recipe" indexes into this list, so its order must be exactly what the
        // Recipes page shows: newest ORIGINAL first, each followed by its adapted variants — a variant
        // saved yesterday still nests under its original, it doesn't jump to the top of the count.
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe { Name = "Oldest Original", SavedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) });
            db.Recipes.Add(new Recipe { Name = "Newest Original", SavedAt = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
            var parent = await db.Recipes.SingleAsync(r => r.Name == "Oldest Original");
            db.Recipes.Add(new Recipe
            {
                Name = "Oldest's Adapted Variant",
                SavedAt = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero),
                ParentRecipeId = parent.Id,
            });
            await db.SaveChangesAsync();
        }

        var refs = await _store.GetRecipesAsync();

        Assert.Equal(
            new[] { "Newest Original", "Oldest Original", "Oldest's Adapted Variant" },
            refs.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task Adding_a_purchase_to_a_tracked_product_reports_no_retrack()
    {
        int milkId;
        await using (var db = _db.CreateDbContext())
        {
            var milk = new Product { Name = "Whole Milk" }; // IsTracked defaults to true
            db.Products.Add(milk);
            await db.SaveChangesAsync();
            milkId = milk.Id;
        }

        var retracked = await _store.AddPurchaseAsync(milkId, new DateOnly(2026, 7, 6), 1m);

        Assert.False(retracked);
        await using var read = _db.CreateDbContext();
        Assert.Equal(1, await read.PurchaseEvents.CountAsync(pe => pe.ProductId == milkId));
    }
}
