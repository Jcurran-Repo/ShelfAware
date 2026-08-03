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

    /// <summary>A counted product with an established count and one recorded purchase.</summary>
    private async Task<(int ProductId, int PurchaseId)> CountedWithPurchase(decimal onHand, decimal bought)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = "Beef Chuck Roast",
            Category = Category.Meat,
            TrackQuantity = true,
            QuantityOnHand = onHand,
            QuantityCountedAt = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var purchase = new PurchaseEvent
        {
            ProductId = product.Id,
            PurchasedAt = new DateOnly(2026, 7, 1),
            Quantity = bought,
            Source = PurchaseSource.Receipt,
        };
        db.PurchaseEvents.Add(purchase);
        await db.SaveChangesAsync();
        return (product.Id, purchase.Id);
    }

    private async Task<Product> Reload(int productId)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
    }

    [Fact]
    public async Task Correcting_a_purchase_moves_the_count_by_the_difference()
    {
        // §13.6: a misread 12 that should have been 2 takes ten off the shelf as well as the history.
        // Fixing one and not the other would just relocate the error.
        var (productId, purchaseId) = await CountedWithPurchase(onHand: 14, bought: 12);

        Assert.True(await _store.SetPurchaseQuantityAsync(purchaseId, 2));

        var product = await Reload(productId);
        Assert.Equal(4m, product.QuantityOnHand);
        // NOT an attestation: the person corrected what the RECEIPT said, not what they can see, so the
        // staleness check keeps measuring from their last real look.
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), product.QuantityCountedAt);
        await using var db = _db.CreateDbContext();
        Assert.Equal(2m, (await db.PurchaseEvents.AsNoTracking().SingleAsync(p => p.Id == purchaseId)).Quantity);
    }

    [Fact]
    public async Task Correcting_upward_adds_the_difference()
    {
        var (productId, purchaseId) = await CountedWithPurchase(onHand: 3, bought: 1);

        Assert.True(await _store.SetPurchaseQuantityAsync(purchaseId, 4));

        Assert.Equal(6m, (await Reload(productId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_purchase_of_none_is_refused_rather_than_clamped()
    {
        // Silently turning a typed 0 into 1 is how the app would start disagreeing with the person who
        // typed it. Removing a purchase entirely is the receipt's job.
        var (productId, purchaseId) = await CountedWithPurchase(onHand: 5, bought: 2);

        Assert.False(await _store.SetPurchaseQuantityAsync(purchaseId, 0));
        Assert.False(await _store.SetPurchaseQuantityAsync(purchaseId, -3));

        Assert.Equal(5m, (await Reload(productId)).QuantityOnHand); // untouched
    }

    [Fact]
    public async Task Correcting_a_purchase_on_an_uncounted_product_only_fixes_the_history()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Bananas", Category = Category.Produce });
            await db.SaveChangesAsync();
        }
        int productId, purchaseId;
        await using (var db = _db.CreateDbContext())
        {
            productId = (await db.Products.SingleAsync(p => p.Name == "Bananas")).Id;
            var purchase = new PurchaseEvent
            {
                ProductId = productId,
                PurchasedAt = new DateOnly(2026, 7, 1),
                Quantity = 9,
                Source = PurchaseSource.Receipt,
            };
            db.PurchaseEvents.Add(purchase);
            await db.SaveChangesAsync();
            purchaseId = purchase.Id;
        }

        Assert.True(await _store.SetPurchaseQuantityAsync(purchaseId, 3));

        Assert.Null((await Reload(productId)).QuantityOnHand); // never opted in, still unknown
        await using var read = _db.CreateDbContext();
        Assert.Equal(3m, (await read.PurchaseEvents.AsNoTracking().SingleAsync(p => p.Id == purchaseId)).Quantity);
    }

    [Fact]
    public async Task A_human_setting_the_count_to_zero_records_running_out()
    {
        // §13.4 through the real store: a person's zero writes the OutNow the burn rate learns from.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetQuantityAsync(productId, 0));

        await using var db = _db.CreateDbContext();
        var signal = Assert.Single(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync());
        Assert.Equal(SignalKind.OutNow, signal.Kind);
    }

    [Fact]
    public async Task A_count_above_zero_records_no_outage()
    {
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetQuantityAsync(productId, 5));

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync());
    }

    [Fact]
    public async Task An_absolute_count_below_zero_is_refused_rather_than_clamped()
    {
        // The same rule SetPurchaseQuantityAsync follows: "-5 on hand" is a number nobody means, and
        // clamping it to 0 would file an OutNow (§13.4) off a typo — a fake outage in the cadence
        // engine, which is the one thing the asserted/derived distinction exists to prevent.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.False(await _store.SetQuantityAsync(productId, -5));

        var product = await Reload(productId);
        Assert.Equal(2m, product.QuantityOnHand); // untouched
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync());
    }

    [Fact]
    public async Task A_relative_move_past_zero_still_lands_at_none_and_reports_it()
    {
        // Relative is the case where the clamp IS right: "used two" against a count of one legitimately
        // means there is nothing left, and the person saying it is looking at the shelf.
        var (productId, _) = await CountedWithPurchase(onHand: 1, bought: 1);

        Assert.True(await _store.SetQuantityAsync(productId, -2, relative: true));

        Assert.Equal(0m, (await Reload(productId)).QuantityOnHand);
        await using var db = _db.CreateDbContext();
        Assert.Equal(SignalKind.OutNow,
            Assert.Single(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync()).Kind);
    }

    [Fact]
    public async Task A_relative_move_refuses_against_a_count_that_was_never_established()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Rice", Category = Category.Pantry, TrackQuantity = true });
            await db.SaveChangesAsync();
        }
        int riceId;
        await using (var db = _db.CreateDbContext())
        {
            riceId = (await db.Products.SingleAsync(p => p.Name == "Rice")).Id;
        }

        // "Used two" needs something to be relative TO; inventing a baseline is the error §13.2 avoids.
        Assert.False(await _store.SetQuantityAsync(riceId, -2, relative: true));
        Assert.Null((await Reload(riceId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_relative_move_does_not_renew_the_counts_credibility()
    {
        // "Used one" states a delta, not a level — the person saw what they took, not the rows behind
        // it. If it re-anchored the attestation, a household dutifully tapping "Used one" would keep a
        // count believed forever without anyone looking, and the drift check could never fire.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetQuantityAsync(productId, -1, relative: true));

        var product = await Reload(productId);
        Assert.Equal(1m, product.QuantityOnHand);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), product.QuantityCountedAt);
    }

    [Fact]
    public async Task An_absolute_count_re_anchors_the_attestation()
    {
        // The counterpart: a stated total IS a look at the shelf, so the clock moves with it.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetQuantityAsync(productId, 5));

        var product = await Reload(productId);
        Assert.Equal(5m, product.QuantityOnHand);
        Assert.True(product.QuantityCountedAt > new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Stop_counting_keeps_the_number_dormant()
    {
        // v3.6's toggle semantics: off is dormant, not destructive. The pair stays for the product
        // page to attribute ("you counted 2 on Mar 1"); TrackQuantity off is what stops the believing.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetQuantityAsync(productId, 0, stopCounting: true));

        var product = await Reload(productId);
        Assert.False(product.TrackQuantity);
        Assert.Equal(2m, product.QuantityOnHand);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), product.QuantityCountedAt);
        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.Where(s => s.ProductId == productId).ToListAsync());
    }

    [Fact]
    public async Task A_fresh_absolute_count_resumes_a_dormant_product()
    {
        // The recovery path the chat refusal points at: after stop-counting, a stated TOTAL opts the
        // product back in. If the dormancy refusal ever spread to absolutes, this is what goes red.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);
        Assert.True(await _store.SetQuantityAsync(productId, 0, stopCounting: true));

        Assert.True(await _store.SetQuantityAsync(productId, 4));

        var product = await Reload(productId);
        Assert.True(product.TrackQuantity);
        Assert.Equal(4m, product.QuantityOnHand);
    }

    [Fact]
    public async Task A_relative_move_refuses_against_a_dormant_count_and_leaves_it_frozen()
    {
        // Found by the 7/30 audit: stop counting, then a habitual "used one" — the dormant pair is
        // HISTORY ("you counted 2 on Mar 1") and a delta must not edit it; the product page's
        // attribution stays true only while nothing moves the frozen number. Resuming is a fresh count.
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);
        Assert.True(await _store.SetQuantityAsync(productId, 0, stopCounting: true));

        Assert.False(await _store.SetQuantityAsync(productId, -1, relative: true));

        var product = await Reload(productId);
        Assert.False(product.TrackQuantity);
        Assert.Equal(2m, product.QuantityOnHand);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), product.QuantityCountedAt);
    }

    [Fact]
    public async Task Set_default_unit_trims_and_stores_the_label()
    {
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);

        Assert.True(await _store.SetDefaultUnitAsync(productId, "  lb "));

        Assert.Equal("lb", (await Reload(productId)).DefaultUnit);
    }

    [Fact]
    public async Task Set_default_unit_clears_on_blank()
    {
        var (productId, _) = await CountedWithPurchase(onHand: 2, bought: 2);
        Assert.True(await _store.SetDefaultUnitAsync(productId, "lb"));

        Assert.True(await _store.SetDefaultUnitAsync(productId, "   "));

        Assert.Null((await Reload(productId)).DefaultUnit);
    }

    [Fact]
    public async Task Set_default_unit_reports_false_for_an_unknown_product()
    {
        Assert.False(await _store.SetDefaultUnitAsync(999_999, "lb"));
    }

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
