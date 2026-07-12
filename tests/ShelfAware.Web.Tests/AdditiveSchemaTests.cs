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
}
