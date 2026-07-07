using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;

namespace ShelfAware.Web.Data;

/// <summary><see cref="IAppSettings"/> over the app's own SQLite DB (a tiny key/value table).
/// Per household since v3 — an implementation detail of the scoped factory; the interface is unchanged.</summary>
public class EfAppSettings(IHouseholdDbFactory dbFactory) : IAppSettings
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value ?? "" });
        }
        else
        {
            setting.Value = value ?? "";
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
