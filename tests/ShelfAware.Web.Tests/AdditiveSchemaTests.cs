using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>Post-v3 additive migrations: an existing DB missing a later column gets it on startup,
/// and re-running is a no-op (Apply runs on every boot).</summary>
public class AdditiveSchemaTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Adds_a_missing_column_to_an_older_db_and_is_idempotent()
    {
        await using var db = _db.CreateDbContext();
        // Simulate a pre-2026-07-12 DB: EnsureCreated built the current schema, so drop the column
        // the way an older file simply wouldn't have it.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Receipts DROP COLUMN VerifiedForEval;");

        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db); // second boot — must be a no-op, not a duplicate-column error

        // EF can query through the column again, and the DEFAULT backfilled existing rows as false.
        Assert.Empty(await db.Receipts.Where(r => r.VerifiedForEval).ToListAsync());
    }

    [Fact]
    public async Task Adds_the_expiration_columns_to_a_pre_expiration_db()
    {
        await using var db = _db.CreateDbContext();
        // Simulate a pre-2026-07-18 DB (built before the expiration-date feature).
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE ReceiptLines DROP COLUMN ExpirationDate;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE PurchaseEvents DROP COLUMN ExpirationDate;");

        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db); // idempotent on the next boot

        // EF queries through both columns again; pre-existing rows read as NULL (no date recorded).
        Assert.Empty(await db.ReceiptLines.Where(l => l.ExpirationDate != null).ToListAsync());
        Assert.Empty(await db.PurchaseEvents.Where(p => p.ExpirationDate != null).ToListAsync());
    }
}
