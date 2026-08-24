using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Billing;

namespace ShelfAware.Web.Auth;

/// <summary>
/// The credit ledger's read/write path (docs/subscription-plan.md §4 — the money record). Append-only:
/// a balance is the SUM of a household's entries, never a mutated running total, so there is no
/// read-modify-write race to lose. auth.db has no tenancy query filter, so every method hand-scopes its
/// WHERE to the household id (the ApiTokenService pattern).
///
/// ⚠️ The balance is read FRESH on every call, never cached for a scope — unlike phase 1's boolean tier,
/// a balance changes with each AI call, and a Blazor circuit can live for hours, so a per-scope cache
/// would let one long session overspend (the gate flag carried on IEntitlements). Phase 2 does not yet
/// ENFORCE the balance (no gating); it records and displays it, so the math can be proven on real usage.
/// </summary>
public sealed class CreditLedger(IDbContextFactory<AuthDbContext> dbFactory)
{
    /// <summary>The household's balance in retail micros = the sum of its ledger entries (empty → 0).</summary>
    public async Task<long> GetBalanceMicrosAsync(string householdId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CreditLedger
            .Where(e => e.HouseholdId == householdId)
            .SumAsync(e => e.AmountMicros, cancellationToken);
    }

    /// <summary>A household's ledger entries, oldest first (by Id — SQLite can't ORDER BY a
    /// DateTimeOffset, and insert order IS chronological). For the data export: the ledger is the
    /// household's money record, so it's part of "download my data".</summary>
    public async Task<IReadOnlyList<CreditLedgerEntry>> ListForHouseholdAsync(
        string householdId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CreditLedger.AsNoTracking()
            .Where(e => e.HouseholdId == householdId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Append a CONSUMPTION entry (stored negative) — an AI call drawing the balance down.
    /// <paramref name="retailMicros"/> is the positive retail amount; a non-positive amount (a free or
    /// cached call) records nothing.</summary>
    public async Task RecordConsumptionAsync(
        string householdId, long retailMicros, string? reason, CancellationToken cancellationToken = default)
    {
        if (retailMicros <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Consumption,
            AmountMicros = -retailMicros,
            Reason = reason,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Append a GRANT entry (positive) — the welcome grant on a standalone context, or an admin
    /// comp later. A non-positive amount records nothing.</summary>
    public async Task GrantAsync(
        string householdId, long amountMicros, string? reason, CancellationToken cancellationToken = default)
    {
        if (amountMicros <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountMicros = amountMicros,
            Reason = reason,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The welcome-grant entry for a new household — a FACTORY, so a registration can add it to
    /// its OWN context (atomic with creating the household) while "what the welcome grant is" stays a
    /// single definition. Amount is the configured cost-dollars × markup (0 → no entry worth adding).</summary>
    public static CreditLedgerEntry? WelcomeGrant(string householdId, BillingOptions options)
    {
        var amount = AiPricing.WelcomeGrantRetailMicros(options);
        return amount <= 0 ? null : new CreditLedgerEntry
        {
            HouseholdId = householdId,
            Kind = CreditEntryKind.Grant,
            AmountMicros = amount,
            Reason = "Welcome grant",
        };
    }
}
