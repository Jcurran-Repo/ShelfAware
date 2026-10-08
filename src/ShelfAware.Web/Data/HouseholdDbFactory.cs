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

/// <param name="member">Who inside the household is asking — scopes the per-person tables (the meal
/// journal). Required, so no construction can forget it and quietly get a context that sees no one's meals.
/// A context that knows no person reads nothing from those tables and refuses to write them
/// (<see cref="ShelfAwareDbContext.MemberId"/>).</param>
public sealed class HouseholdDbFactory(
    IDbContextFactory<ShelfAwareDbContext> inner, ICurrentHousehold household, ICurrentMember member)
    : IHouseholdDbFactory
{
    public async Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        var db = await inner.CreateDbContextAsync(cancellationToken);
        db.HouseholdId = await household.GetRequiredIdAsync(cancellationToken);
        db.MemberId = await member.GetMemberIdAsync(cancellationToken);
        return db;
    }
}
