using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The one-off v3.4 rebuild that relaxes Households.InviteCode to nullable and retires the codes minted
/// under the old rules. Runs against real SQLite (TestAuthDb) because every claim here is about what
/// SQLite does — NOT NULL, a unique index over NULLs, and a table swap — none of which a fake would model.
/// </summary>
public class NullableInviteCodeMigrationTests : IDisposable
{
    private readonly TestAuthDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>The pre-v3.4 shape: EnsureCreated builds the CURRENT (nullable) schema, so an old DB has to
    /// be reconstructed to test against. This is the live 7/15 shape exactly — four v3 columns plus the
    /// three AdditiveSchema ALTERed on, InviteCode NOT NULL, unfiltered unique index.</summary>
    /// <summary>Built by substitution rather than interpolation so it stays an ExecuteSqlRawAsync(string):
    /// the interpolated overload trips EF1002 (SQL injection), which is the right warning to get for a
    /// value from outside and the wrong one to suppress just because this one is a test's own literal.</summary>
    private const string OldDdlTemplate = """
        CREATE TABLE "Households" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Households" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "InviteCode" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "InviteExpiresAt" TEXT NULL,
            "InviteMaxUses" INTEGER NULL,
            "InviteUseCount" INTEGER NOT NULL DEFAULT 0/*EXTRAS*/
        );
        """;

    private static async Task GiveItTheOldSchemaAsync(AuthDbContext db, params string[] extraColumns)
    {
        var extras = extraColumns.Length == 0 ? "" : ", " + string.Join(", ", extraColumns);
        await db.Database.ExecuteSqlRawAsync(@"DROP TABLE ""Households"";");
        await db.Database.ExecuteSqlRawAsync(OldDdlTemplate.Replace("/*EXTRAS*/", extras));
        await db.Database.ExecuteSqlRawAsync(
            @"CREATE UNIQUE INDEX ""IX_Households_InviteCode"" ON ""Households"" (""InviteCode"");");
    }

    private static async Task<bool> InviteCodeIsNullableAsync(AuthDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""notnull"" FROM pragma_table_info('Households') WHERE name = 'InviteCode';";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 0;
    }

    [Fact]
    public async Task Relaxes_the_column_wipes_the_codes_and_is_idempotent()
    {
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        db.Households.Add(new Household
        {
            Name = "The Currans",
            InviteCode = "PERMANENT1",
            InviteMaxUses = null,      // the old default: unlimited
            InviteUseCount = 4,
        });
        await db.SaveChangesAsync();
        Assert.False(await InviteCodeIsNullableAsync(db));

        NullableInviteCodeMigration.Apply(db);
        NullableInviteCodeMigration.Apply(db); // second boot — the guard must make this a no-op

        Assert.True(await InviteCodeIsNullableAsync(db));

        await using var fresh = _db.CreateDbContext();
        var saved = fresh.Households.Single();
        Assert.Equal("The Currans", saved.Name);   // the household itself survives
        Assert.Null(saved.InviteCode);             // its permanent code does not
        Assert.Null(saved.InviteMaxUses);
        Assert.Null(saved.InviteExpiresAt);
        Assert.Equal(0, saved.InviteUseCount);
    }

    [Fact]
    public async Task Members_keep_their_household()
    {
        // The wipe revokes a key; it must not evict the people who already used it. Membership lives on
        // AspNetUsers.HouseholdId, which the rebuild never touches — pinned because "wipe the codes" is
        // one careless JOIN away from meaning something much worse.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        var household = new Household { Name = "Home", InviteCode = "PERMANENT1" };
        db.Households.Add(household);
        db.Users.Add(new AppUser { UserName = "a@example.com", Email = "a@example.com", HouseholdId = household.Id });
        await db.SaveChangesAsync();

        NullableInviteCodeMigration.Apply(db);

        await using var fresh = _db.CreateDbContext();
        Assert.Equal(household.Id, fresh.Users.Single().HouseholdId);
    }

    [Fact]
    public async Task The_rebuilt_table_admits_many_code_less_households()
    {
        // The entire point of the rebuild: the unique index has to keep rejecting duplicate codes while
        // letting every household have none. If the index came back wrong, the SECOND code-less household
        // is where it would show — which on a live box is the second person to sign up.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        db.Households.Add(new Household { Name = "Home", InviteCode = "PERMANENT1" });
        await db.SaveChangesAsync();

        NullableInviteCodeMigration.Apply(db);

        await using var fresh = _db.CreateDbContext();
        fresh.Households.Add(new Household { Name = "Second" });
        fresh.Households.Add(new Household { Name = "Third" });
        await fresh.SaveChangesAsync(); // three NULLs, no collision

        Assert.Equal(3, fresh.Households.Count());
    }

    [Fact]
    public async Task The_rebuilt_table_still_rejects_a_duplicate_code()
    {
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db);
        NullableInviteCodeMigration.Apply(db);

        await using var fresh = _db.CreateDbContext();
        fresh.Households.Add(new Household { Name = "Home", InviteCode = "SAMECODE22" });
        fresh.Households.Add(new Household { Name = "Elsewhere", InviteCode = "SAMECODE22" });

        await Assert.ThrowsAsync<DbUpdateException>(() => fresh.SaveChangesAsync());
    }

    [Fact]
    public async Task A_fresh_database_is_left_alone()
    {
        // EnsureCreated already builds the nullable column, so the guard must see no work rather than
        // rebuild a table it has no reason to touch.
        await using var db = _db.CreateDbContext();
        db.Households.Add(new Household { Name = "Home", InviteCode = "KEEPME1234", InviteMaxUses = 1 });
        await db.SaveChangesAsync();

        NullableInviteCodeMigration.Apply(db);

        await using var fresh = _db.CreateDbContext();
        var saved = fresh.Households.Single();
        Assert.Equal("KEEPME1234", saved.InviteCode); // NOT wiped: this code was minted under the new rules
        Assert.Equal(1, saved.InviteMaxUses);
    }

    [Fact]
    public async Task An_unrecognised_column_stops_the_migration_rather_than_dropping_it()
    {
        // The rebuild copies columns by name, so a Household property added later — on a deployment that
        // hadn't migrated yet — would be silently DROPPED. Fail loudly and name it instead.
        await using var db = _db.CreateDbContext();
        await GiveItTheOldSchemaAsync(db, extraColumns: @"""FavouriteColour"" TEXT NULL");

        var ex = Assert.Throws<InvalidOperationException>(() => NullableInviteCodeMigration.Apply(db));

        Assert.Contains("FavouriteColour", ex.Message);
        // And it stopped BEFORE touching anything.
        Assert.False(await InviteCodeIsNullableAsync(db));
    }
}
