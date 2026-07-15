using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Pins the write side of v3 tenancy. The global query filter protects READS, but EF builds updates and
/// deletes from the change tracker keyed on the primary key alone — no filter is ever consulted — so a
/// detached entity carrying another household's id would issue a valid cross-tenant UPDATE/DELETE.
/// These tests are the reason that can't happen.
/// </summary>
public class HouseholdWriteGuardTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<Product> SeedProductAsync(string household, string name)
    {
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        var product = new Product { Name = name };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task An_insert_is_stamped_with_the_contexts_household()
    {
        var product = await SeedProductAsync("hh-a", "Whole Milk");

        Assert.Equal("hh-a", product.HouseholdId);
    }

    [Fact]
    public async Task An_insert_naming_another_household_is_refused()
    {
        _db.HouseholdId = "hh-a";
        await using var db = _db.CreateDbContext();
        db.Products.Add(new Product { Name = "Whole Milk", HouseholdId = "hh-b" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("hh-b", ex.Message);
    }

    [Fact]
    public async Task Updating_another_households_row_via_a_detached_entity_is_refused()
    {
        // The Products page's Save() attaches a detached product and marks it Modified — a write keyed on
        // the PK alone. Handed one from another household, it must not become a cross-tenant UPDATE.
        var foreign = await SeedProductAsync("hh-b", "Whole Milk");
        foreign.Name = "Renamed by A";

        _db.HouseholdId = "hh-a";
        await using var db = _db.CreateDbContext();
        db.Attach(foreign);
        db.Entry(foreign).State = EntityState.Modified;

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        _db.HouseholdId = "hh-b";
        await using var check = _db.CreateDbContext();
        Assert.Equal("Whole Milk", (await check.Products.SingleAsync()).Name);
    }

    [Fact]
    public async Task Deleting_another_households_row_via_a_detached_entity_is_refused()
    {
        // The shape the pages' `Remove(await Find(id) ?? f)` fallback would have taken: Find returns null
        // for a foreign row (it IS filtered), and removing the detached object then deletes it unfiltered.
        var foreign = await SeedProductAsync("hh-b", "Whole Milk");

        _db.HouseholdId = "hh-a";
        await using var db = _db.CreateDbContext();
        db.Products.Remove(foreign);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        _db.HouseholdId = "hh-b";
        await using var check = _db.CreateDbContext();
        Assert.Equal(1, await check.Products.CountAsync());
    }

    [Fact]
    public async Task Ordinary_updates_and_deletes_of_your_own_rows_still_work()
    {
        var product = await SeedProductAsync("hh-a", "Whole Milk");

        _db.HouseholdId = "hh-a";
        await using (var db = _db.CreateDbContext())
        {
            var mine = await db.Products.SingleAsync();
            mine.Name = "2% Milk";
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal("2% Milk", (await db.Products.SingleAsync()).Name);
            db.Products.Remove(await db.Products.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using (var db = _db.CreateDbContext())
        {
            Assert.Empty(await db.Products.ToListAsync());
        }

        Assert.Equal("hh-a", product.HouseholdId);
    }

    [Fact]
    public async Task An_unscoped_context_still_writes_exactly_what_the_caller_made()
    {
        // Bootstrap/test escape hatch: no household means no stamping and no checking, as before.
        await using var db = _db.CreateUnscopedContext();
        db.Products.Add(new Product { Name = "Orphan", HouseholdId = "hh-b" });
        await db.SaveChangesAsync();

        await using var raw = _db.CreateUnscopedContext();
        Assert.Equal("hh-b", (await raw.Products.IgnoreQueryFilters().SingleAsync()).HouseholdId);
    }
}
