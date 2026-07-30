using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>Drives the REAL <see cref="EfAppSettings"/> — the first version of this file re-implemented
/// its subject's two methods as private helpers, so a broken upsert or null-handling change kept every
/// test green (the 7/30 audit's "subject re-implemented in the test" class). The per-household
/// semantics (composite (HouseholdId, Key) + query filter) are asserted through the class that
/// production actually calls.</summary>
public class EfAppSettingsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EfAppSettings _settings;

    public EfAppSettingsTests() => _settings = new EfAppSettings(_db);

    public void Dispose() => _db.Dispose();

    private async Task<string?> AsHousehold(string household, Func<Task<string?>> act)
    {
        var previous = _db.HouseholdId;
        _db.HouseholdId = household;
        try { return await act(); }
        finally { _db.HouseholdId = previous; }
    }

    private Task<string?> SetAsync(string household, string key, string? value) =>
        AsHousehold(household, async () => { await _settings.SetAsync(key, value); return null; });

    private Task<string?> GetAsync(string household, string key) =>
        AsHousehold(household, () => _settings.GetAsync(key));

    [Fact]
    public async Task The_same_key_holds_a_different_value_per_household()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");
        await SetAsync("hh-b", "ImportMode", "Review");

        Assert.Equal("Smart", await GetAsync("hh-a", "ImportMode"));
        Assert.Equal("Review", await GetAsync("hh-b", "ImportMode"));
    }

    [Fact]
    public async Task Setting_an_existing_key_updates_in_place_rather_than_stacking_rows()
    {
        // The upsert's update branch — the half the old helpers never exercised. A second row for the
        // same (household, key) would make every later read ambiguous.
        await SetAsync("hh-a", "ImportMode", "Smart");
        await SetAsync("hh-a", "ImportMode", "Auto");

        Assert.Equal("Auto", await GetAsync("hh-a", "ImportMode"));
        await using var raw = _db.CreateUnscopedContext();
        Assert.Equal(1, await raw.AppSettings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_null_value_is_stored_as_empty_not_dropped()
    {
        // The class's documented shape: SetAsync(key, null) stores "" — so a caller can distinguish
        // "explicitly cleared" (empty string) from "never set" (null from GetAsync).
        await SetAsync("hh-a", "ReceiptFolder", null);

        Assert.Equal("", await GetAsync("hh-a", "ReceiptFolder"));
        Assert.Null(await GetAsync("hh-a", "NeverSet"));
    }

    [Fact]
    public async Task Stamping_fills_the_household_key_member_on_insert()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");

        await using var raw = _db.CreateUnscopedContext();
        var row = await raw.AppSettings.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("hh-a", row.HouseholdId);
    }

    [Fact]
    public async Task A_household_cannot_read_anothers_setting()
    {
        await SetAsync("hh-a", "ImportMode", "Smart");

        Assert.Null(await GetAsync("hh-b", "ImportMode"));
    }

    [Fact]
    public async Task An_unscoped_context_sees_no_settings_not_even_ownerless_ones()
    {
        // Background code that forgot to pick a household reads NOTHING rather than some shared
        // bucket. (EF folds the filter to FALSE for a null-household context on this non-nullable key
        // column.) This is a context guarantee rather than an EfAppSettings branch, but it's the
        // reason GetAsync above can be trusted — kept here beside the reads it protects.
        await SetAsync("hh-a", "ImportMode", "Smart");
        await using (var writer = _db.CreateUnscopedContext())
        {
            writer.AppSettings.Add(new AppSetting { Key = "Orphan", Value = "x" });
            await writer.SaveChangesAsync();
        }

        await using var unscoped = _db.CreateUnscopedContext();
        Assert.Empty(await unscoped.AppSettings.ToListAsync());
        Assert.Equal(2, await unscoped.AppSettings.IgnoreQueryFilters().CountAsync());
    }
}
