using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;
using ShelfAware.Web.Diagnostics;

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

    [Fact]
    public async Task Creates_the_BugReports_table_on_an_older_db_with_the_fresh_schema()
    {
        await using var db = _db.CreateDbContext();
        var fresh = await TableSchemaAsync(db, "BugReports");
        Assert.NotEmpty(fresh);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE BugReports;");
        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db);

        Assert.Equal(fresh, await TableSchemaAsync(db, "BugReports"));

        db.BugReports.Add(new BugReport { Body = "It looked wrong", CreatedAt = DateTimeOffset.Now });
        await db.SaveChangesAsync();
        Assert.Single(await db.BugReports.ToListAsync());
    }

    [Fact]
    public async Task Creates_the_ErrorLog_table_on_an_older_auth_db_with_the_fresh_schema()
    {
        // The auth-side twin of the pantry table tests: the error log lives in auth.db (operator
        // data), and a live deployment's auth file predates it.
        using var authDb = new TestAuthDb();
        await using var db = authDb.CreateDbContext();
        var fresh = await TableSchemaAsync(db, "ErrorLog");
        Assert.NotEmpty(fresh);

        await db.Database.ExecuteSqlRawAsync("DROP TABLE ErrorLog;");
        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db);

        Assert.Equal(fresh, await TableSchemaAsync(db, "ErrorLog"));

        db.ErrorLog.Add(new ErrorLogEntry
        {
            Fingerprint = "F1", Level = "Error", Category = "Test", LastMessage = "boom",
            Count = 1, FirstSeenAt = DateTimeOffset.Now, LastSeenAt = DateTimeOffset.Now,
        });
        await db.SaveChangesAsync();
        Assert.Single(await db.ErrorLog.ToListAsync());
    }

    [Fact]
    public async Task Adds_the_quantity_columns_to_a_pre_counting_db()
    {
        await using var db = _db.CreateDbContext();
        // The schema EnsureCreated just built is the reference for what these columns should BE.
        var fresh = await ColumnTypesAsync(db, "Products");

        // Simulate a pre-2026-07-28 DB (built before quantity on hand existed).
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Products DROP COLUMN TrackQuantity;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Products DROP COLUMN QuantityOnHand;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Products DROP COLUMN QuantityCountedAt;");

        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db); // second boot — a no-op, not a duplicate-column error

        // Same DECLARED TYPES as a fresh file. This is the pin a can-EF-query check alone would miss:
        // an ALTER whose type guess differs from EF's generated DDL still "works" under SQLite's dynamic
        // typing right up until it silently truncates something. Compared per column rather than as a
        // whole CREATE TABLE because ADD COLUMN appends, so the column ORDER legitimately differs.
        Assert.Equal(fresh, await ColumnTypesAsync(db, "Products"));

        // Existing rows land opted-out with an unknown count — today's behaviour, exactly.
        var product = new Product { Name = "Beef Chuck Roast", Category = Category.Meat };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.False(stored.TrackQuantity);
        Assert.Null(stored.QuantityOnHand);
        Assert.Null(stored.QuantityCountedAt);

        // And the count round-trips a DECIMAL rather than truncating to a whole number — the failure a
        // wrong column type would actually produce, on exactly the weight items counting is for.
        product.TrackQuantity = true;
        product.QuantityOnHand = 2.34m;
        product.QuantityCountedAt = new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.FromHours(-5));
        await db.SaveChangesAsync();

        var counted = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.True(counted.TrackQuantity);
        Assert.Equal(2.34m, counted.QuantityOnHand);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.FromHours(-5)), counted.QuantityCountedAt);
    }

    [Fact]
    public async Task Adds_the_confirmed_at_column_to_a_pre_v41_db()
    {
        await using var db = _db.CreateDbContext();
        var fresh = await ColumnTypesAsync(db, "Receipts");

        // Simulate a DB built before v4.1's confirm timestamp existed.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Receipts DROP COLUMN ConfirmedAt;");

        AdditiveSchema.Apply(db);
        AdditiveSchema.Apply(db); // second boot — a no-op, not a duplicate-column error

        Assert.Equal(fresh, await ColumnTypesAsync(db, "Receipts"));

        // A receipt written without the stamp reads back NULL — "no moment to compare", which removal
        // treats as a pre-v4.1 confirm and subtracts exactly as it always did.
        var receipt = new Receipt { ImagePath = "confirmedat-test", Status = ReceiptStatus.Confirmed };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        Assert.Null((await db.Receipts.AsNoTracking().SingleAsync(r => r.Id == receipt.Id)).ConfirmedAt);
    }

    /// <summary>Each column's declared type, keyed by name — order-independent, so it survives the fact
    /// that ADD COLUMN appends while EnsureCreated writes the model's order.</summary>
    private static async Task<Dictionary<string, string>> ColumnTypesAsync(ShelfAwareDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        // Type and nullability only — deliberately NOT the DEFAULT clause. SQLite requires a default to
        // ADD a NOT NULL column, while EnsureCreated has no reason to emit one, so that difference is
        // inherent to migrating rather than a drift worth failing on. Type and notnull are the parts that
        // change behaviour.
        cmd.CommandText = $"SELECT name, type, \"notnull\" FROM pragma_table_info('{table}');";
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = $"{reader.GetString(1)}|notnull={reader.GetInt32(2)}";
        }
        return columns;
    }

    /// <summary>Every sqlite_master row about the table (itself and each index), name-ordered,
    /// whitespace-normalized — a comparable fingerprint of the physical schema.</summary>
    private static async Task<List<string>> TableSchemaAsync(DbContext db, string table)
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
