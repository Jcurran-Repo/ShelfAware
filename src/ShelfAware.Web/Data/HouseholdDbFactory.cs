using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Data;

/// <summary>The way pages and services get a pantry DbContext: pre-scoped to the current household,
/// so every query filters to it and every insert is stamped with it. A DELIBERATELY separate
/// interface from <c>IDbContextFactory</c> — each call site visibly chooses scoped (this) or raw
/// (bootstrap-only), and nothing gets an unscoped context by accident. Async-only on purpose: every
/// production call site already is.</summary>
public interface IHouseholdDbFactory
{
    Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}

public sealed class HouseholdDbFactory(
    IDbContextFactory<ShelfAwareDbContext> inner, ICurrentHousehold household) : IHouseholdDbFactory
{
    public async Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var db = await inner.CreateDbContextAsync(cancellationToken);
        db.HouseholdId = await household.GetRequiredIdAsync(cancellationToken);
        return db;
    }
}
