using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>Pins the per-household settings semantics introduced in v3 (composite (HouseholdId, Key)
/// key + query filter): households don't see each other's values, and an UNSCOPED context sees no
/// settings at all — the safe default for background code that forgot to pick a household.</summary>
public class EfAppSettingsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task SetAsync(string household, string key, string value)
    {
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        await db.SaveChangesAsync();
    }

    private async Task<string?> GetAsync(string household, string key)
    {
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value;
    }

    [Fact]
    public async Task The_same_key_holds_a_different_value_per_household()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");
        await SetAsync("hh-b", "ImportMode", "Review");

        Assert.Equal("Smart", await GetAsync("hh-a", "ImportMode"));
        Assert.Equal("Review", await GetAsync("hh-b", "ImportMode"));
    }

    [Fact]
    public async Task Stamping_fills_the_household_key_member_on_insert()
    {
        await SetAsync("hh-a", "ReceiptFolder", @"C:\receipts");

        await using var raw = _db.CreateUnscopedContext();
        var row = await raw.AppSettings.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("hh-a", row.HouseholdId);
    }

    [Fact]
    public async Task An_unscoped_context_sees_no_settings_not_even_ownerless_ones()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");

        // An unscoped write lands in the ownerless "" bucket (stamping no-ops)…
        await using (var writer = _db.CreateUnscopedContext())
        {
            writer.AppSettings.Add(new AppSetting { Key = "Orphan", Value = "x" });
            await writer.SaveChangesAsync();
        }

        // …but even that is invisible without a household: background code that forgot to pick a
        // household reads nothing, rather than some shared bucket. (EF folds the filter to FALSE for
        // a null-household context on this non-nullable key column.)
        await using var unscoped = _db.CreateUnscopedContext();
        Assert.Empty(await unscoped.AppSettings.ToListAsync());
        Assert.Equal(2, await unscoped.AppSettings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_household_cannot_read_anothers_setting()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");

        Assert.Null(await GetAsync("hh-b", "ImportMode"));
    }
}
