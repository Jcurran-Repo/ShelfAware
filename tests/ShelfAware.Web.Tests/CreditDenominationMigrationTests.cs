using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The one-off 2026-09-19 conversion of the credit ledger from retail micros to Shelf Aware credits.
/// Runs against real SQLite because every claim is about what SQLite does — an ALTER, a ROUND, and a
/// transaction around both — none of which a fake would model. This is a MONEY migration: the thing it
/// must never do is leave a household's balance silently wrong.
/// </summary>
public class CreditDenominationMigrationTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private static readonly BillingOptions Billing = new();

    public void Dispose() => _db.Dispose();

    /// <summary>The pre-credit shape the migration actually sees at boot: EnsureCreated builds the CURRENT
    /// schema (both columns), so the old one has to be reconstructed — the table as it stood with
    /// AmountMicros alone.</summary>
    private static async Task GiveItTheOldSchemaAsync(AuthDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""CreditLedger"";");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "CreditLedger" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CreditLedger" PRIMARY KEY AUTOINCREMENT,
                "HouseholdId" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "AmountMicros" INTEGER NOT NULL,
                "Reason" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);
    }

    private static Task SeedOldRowAsync(AuthDbContext db, string household, CreditEntryKind kind, long micros) =>
        db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO ""CreditLedger"" (""HouseholdId"", ""Kind"", ""AmountMicros"", ""Reason"", ""CreatedAt"")
              VALUES ({0}, {1}, {2}, NULL, {3});",
            household, (int)kind, micros, DateTimeOffset.Now.ToString("O"));

    private static async Task<bool> HasCreditsColumnAsync(AuthDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS (SELECT 1 FROM pragma_table_info('CreditLedger') WHERE name = 'AmountCredits');";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    [Fact]
    public async Task Every_existing_row_converts_at_the_anchor_and_the_balance_survives()
    {
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Grant, 1_650_000);       // the old welcome grant
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Consumption, -578);      // one old Haiku round
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Purchase, 5_000_000);    // the old $5 pack
        Assert.False(await HasCreditsColumnAsync(db));

        CreditDenominationMigration.Apply(db, Billing);

        Assert.True(await HasCreditsColumnAsync(db));
        var rows = await db.CreditLedger.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(100, rows[0].AmountCredits);   // $1.65 retail ÷ $0.0165 a credit
        Assert.Equal(0, rows[1].AmountCredits);     // 578 micros was a 29th of a credit — it rounds to none
        Assert.Equal(303, rows[2].AmountCredits);   // the same 303 credits the pack sells for today
        // ⚠️ The direction that matters: nobody LOSES money. The tiny consumption rounding to zero is a
        // rounding IN the household's favour, which is the side to err on for a one-time conversion.
        Assert.Equal(403, rows.Sum(e => e.AmountCredits));
    }

    [Fact]
    public async Task The_original_micros_are_kept_as_the_receipt_for_the_conversion()
    {
        // The audit trail is the whole reason the old column stays: "why is my balance 100?" has an answer
        // in the data rather than in a changelog.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Grant, 1_650_000);

        CreditDenominationMigration.Apply(db, Billing);

        var row = await db.CreditLedger.AsNoTracking().SingleAsync();
        Assert.Equal(1_650_000, row.LegacyAmountMicros);
        Assert.Equal(CreditPricing.CreditsFromRetailMicros(Billing, 1_650_000), row.AmountCredits);
    }

    [Fact]
    public async Task A_negative_row_converts_to_a_negative_credit_count()
    {
        // Consumption and expiry sweeps are stored negative, and the balance is a plain SUM — a conversion
        // that lost the sign would turn every household's spending into a windfall.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Consumption, -330_000);
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Expiry, -165_000);

        CreditDenominationMigration.Apply(db, Billing);

        var rows = await db.CreditLedger.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(-20, rows[0].AmountCredits);
        Assert.Equal(-10, rows[1].AmountCredits);
    }

    [Fact]
    public async Task Running_it_again_converts_nothing_twice()
    {
        // It runs on EVERY boot. A second pass that re-read AmountMicros would be harmless here, but one
        // that re-read the CREDITS and divided again would quietly erase a household's balance over a few
        // restarts — so "already converted" has to be an observed fact, not an assumption.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        await SeedOldRowAsync(db, "hh-a", CreditEntryKind.Grant, 1_650_000);

        CreditDenominationMigration.Apply(db, Billing);
        CreditDenominationMigration.Apply(db, Billing);
        CreditDenominationMigration.Apply(db, Billing);

        Assert.Equal(100, (await db.CreditLedger.AsNoTracking().SingleAsync()).AmountCredits);
    }

    [Fact]
    public async Task A_fresh_database_is_left_alone()
    {
        // EnsureCreated already built the table with both columns, so there is nothing to convert — and a
        // migration that ran anyway would zero the AmountCredits of rows written since boot.
        await using var db = _db.CreateDbContext();
        db.CreditLedger.Add(new CreditLedgerEntry { HouseholdId = "hh-a", Kind = CreditEntryKind.Grant, AmountCredits = 100 });
        await db.SaveChangesAsync();

        CreditDenominationMigration.Apply(db, Billing);

        Assert.Equal(100, (await db.CreditLedger.AsNoTracking().SingleAsync()).AmountCredits);
    }

    [Fact]
    public async Task A_database_with_no_ledger_table_at_all_is_left_alone()
    {
        // A box that predates the ledger entirely: AdditiveSchema.Apply creates the table, and this runs
        // strictly after it — but the guard is what stops a reordering from throwing on every boot.
        await using var db = _db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""CreditLedger"";");

        CreditDenominationMigration.Apply(db, Billing); // no throw

        Assert.False(await HasCreditsColumnAsync(db));
    }
}
