using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
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

    [Fact]
    public async Task Creates_the_MealEvents_table_on_a_pre_meal_log_db_with_the_fresh_schema()
    {
        await using var db = _db.CreateDbContext();
        // What EnsureCreated built in the TestDb constructor is the reference schema.
        var fresh = await TableSchemaAsync(db, "MealEvents");
        Assert.NotEmpty(fresh);

        // Simulate a DB from before the meal log existed, then boot.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE MealEvents;");
        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db); // second boot — a no-op, not a table-exists error

        // The migrated table is IDENTICAL to a fresh file's — same DDL, same indexes. This is the pin
        // on EnsureTable's whole premise (DDL lifted from EF's create script, no second schema copy).
        Assert.Equal(fresh, await TableSchemaAsync(db, "MealEvents"));

        // And it behaves: writes go through, the recipe cascade holds.
        var recipe = new Recipe { Name = "Toast", SavedAt = DateTimeOffset.Now };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        db.MealEvents.Add(new MealEvent { RecipeId = recipe.Id, AteAt = new DateOnly(2026, 7, 18) });
        await db.SaveChangesAsync();
        db.Recipes.Remove(recipe);
        await db.SaveChangesAsync();
        Assert.Empty(await db.MealEvents.ToListAsync());
    }

    [Fact]
    public async Task Creates_the_SavedReports_table_on_an_older_db_with_the_fresh_schema()
    {
        await using var db = _db.CreateDbContext();
        var fresh = await TableSchemaAsync(db, "SavedReports");
        Assert.NotEmpty(fresh);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE SavedReports;");
        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db);

        Assert.Equal(fresh, await TableSchemaAsync(db, "SavedReports"));

        db.SavedReports.Add(new SavedReport { Name = "Snacks", Query = "from=2026-06-01&to=2026-07-18", SavedAt = DateTimeOffset.Now });
        await db.SaveChangesAsync();
        Assert.Single(await db.SavedReports.ToListAsync());
    }

    /// <summary>Every sqlite_master row about the table (itself and each index), name-ordered,
    /// whitespace-normalized — a comparable fingerprint of the physical schema.</summary>
    private static async Task<List<string>> TableSchemaAsync(ShelfAwareDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name = @t AND sql IS NOT NULL ORDER BY name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@t";
        p.Value = table;
        cmd.Parameters.Add(p);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join(' ',
                reader.GetString(0).Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)));
        }
        return rows;
    }
}
