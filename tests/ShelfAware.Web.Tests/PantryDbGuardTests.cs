using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class PantryDbGuardTests
{
    private static (SqliteConnection conn, ShelfAwareDbContext db) OpenRaw()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ShelfAwareDbContext>().UseSqlite(conn).Options;
        return (conn, new ShelfAwareDbContext(options));
    }

    [Fact]
    public void A_pre_household_db_fails_fast_with_instructions()
    {
        var (conn, db) = OpenRaw();
        using (conn)
        using (db)
        {
            // The pre-v3 shape: a Products table with no HouseholdId column.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE \"Products\" (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<InvalidOperationException>(() => PantryDbGuard.ThrowIfPreHouseholdDb(db));
            Assert.Contains("delete shelfaware.db*", ex.Message);
        }
    }

    [Fact]
    public void A_fresh_empty_db_passes()
    {
        var (conn, db) = OpenRaw();
        using (conn)
        using (db)
        {
            PantryDbGuard.ThrowIfPreHouseholdDb(db); // no Products table at all — nothing to object to
        }
    }

    [Fact]
    public void A_v3_db_passes()
    {
        using var testDb = new TestDb(); // EnsureCreated builds the v3 schema (HouseholdId everywhere)
        using var db = testDb.CreateDbContext();
        PantryDbGuard.ThrowIfPreHouseholdDb(db);
    }
}
