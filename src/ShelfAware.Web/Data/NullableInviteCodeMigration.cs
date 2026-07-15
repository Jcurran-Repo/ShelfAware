using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// A ONE-OFF structural migration (v3.4, 2026-07-15): makes <c>Households.InviteCode</c> nullable and
/// retires every code that existed under the old rules.
///
/// This is the documented exception to <see cref="AdditiveSchema"/>, which does additive DEFAULT-valued
/// columns only and says so — deliberately, because that is the one shape of change SQLite can make to a
/// live table in place. Relaxing NOT NULL isn't that shape: SQLite's ALTER TABLE cannot do it at all, so
/// the only route is the documented rebuild (create · copy · drop · rename) below. The alternative was to
/// model "this household has no invite code" as <c>""</c>, which the unique index would allow exactly one
/// household to be — see <see cref="Household.InviteCode"/>. A migration that runs once beat a sentinel
/// every future reader has to be told about.
///
/// The codes are wiped as part of the copy rather than by a second statement, because they are one
/// decision: every existing code was minted permanent and unlimited by rules that no longer exist, so
/// carrying one across would import exactly the credential this change was made to stop issuing. Wiping
/// evicts nobody — membership lives on <c>AspNetUsers.HouseholdId</c>, which this doesn't touch. It only
/// means a household that wants a new member asks for a code first.
///
/// Safe to delete once every deployment has booted past v3.4 — after which the guard below is the only
/// thing it does.
/// </summary>
public static class NullableInviteCodeMigration
{
    /// <summary>The columns this migration knows how to carry across. Asserted rather than assumed: the
    /// rebuild names columns explicitly, so a property added to <see cref="Household"/> later — created by
    /// EnsureCreated on a fresh DB, ALTERed in by <see cref="AdditiveSchema"/> on an existing one — would
    /// be silently DROPPED here on any deployment that hadn't migrated yet. Better a loud failure at
    /// startup naming the problem than a column that quietly disappears from one deployment in ten.</summary>
    private static readonly string[] ExpectedColumns =
        ["Id", "Name", "InviteCode", "CreatedAt", "InviteExpiresAt", "InviteMaxUses", "InviteUseCount"];

    public static void Apply(AuthDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            if (!InviteCodeIsNotNull(conn)) return; // already nullable: a fresh DB, or we've run before.
            AssertKnownSchema(conn);

            // PRAGMA foreign_keys is a no-op inside a transaction, so it has to bracket one. Nothing
            // references Households today (AspNetUsers.HouseholdId is a plain indexed column, not an FK),
            // but the rebuild follows SQLite's documented procedure rather than relying on that staying true.
            Execute(conn, "PRAGMA foreign_keys=off;");
            using (var tx = conn.BeginTransaction())
            {
                // Mirrors the live column order rather than EF's property order: this is the smallest
                // possible delta from what's actually on disk, so the only things that change are
                // InviteCode's nullability and the wiped values. (Column order already differs between a
                // fresh DB and an ALTERed one; nothing addresses these by position.)
                Execute(conn, tx, """
                    CREATE TABLE "Households_new" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Households" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "InviteCode" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "InviteExpiresAt" TEXT NULL,
                        "InviteMaxUses" INTEGER NULL,
                        "InviteUseCount" INTEGER NOT NULL DEFAULT 0
                    );
                    """);

                // The wipe: codes and their limits come across as NULL/0 regardless of what was there.
                Execute(conn, tx, """
                    INSERT INTO "Households_new"
                        ("Id", "Name", "InviteCode", "CreatedAt", "InviteExpiresAt", "InviteMaxUses", "InviteUseCount")
                    SELECT "Id", "Name", NULL, "CreatedAt", NULL, NULL, 0 FROM "Households";
                    """);

                // DROP takes the old indexes with it; the unique one is recreated below to match
                // AuthDbContext.OnModelCreating (unfiltered — NULLs are distinct in SQLite).
                Execute(conn, tx, @"DROP TABLE ""Households"";");
                Execute(conn, tx, @"ALTER TABLE ""Households_new"" RENAME TO ""Households"";");
                Execute(conn, tx, @"CREATE UNIQUE INDEX ""IX_Households_InviteCode"" ON ""Households"" (""InviteCode"");");

                tx.Commit();
            }
            Execute(conn, "PRAGMA foreign_keys=on;");
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>Whether the column still carries the old NOT NULL — the guard that makes this idempotent.
    /// Reads the schema rather than tracking a version number, so it's true exactly when there's work.</summary>
    private static bool InviteCodeIsNotNull(System.Data.Common.DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""notnull"" FROM pragma_table_info('Households') WHERE name = 'InviteCode';";
        var result = cmd.ExecuteScalar();
        // No row at all means no Households table yet — EnsureCreated hasn't run, so there's nothing to
        // rebuild and nothing to complain about.
        return result is not null && result is not DBNull && Convert.ToInt64(result) == 1;
    }

    private static void AssertKnownSchema(System.Data.Common.DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('Households');";
        var actual = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) actual.Add(reader.GetString(0));
        }

        var unknown = actual.Except(ExpectedColumns, StringComparer.Ordinal).ToArray();
        var missing = ExpectedColumns.Except(actual, StringComparer.Ordinal).ToArray();
        if (unknown.Length == 0 && missing.Length == 0) return;

        throw new InvalidOperationException(
            "NullableInviteCodeMigration can't rebuild Households: the table isn't the shape it was written " +
            "for, so copying it column-by-column would lose data. " +
            (unknown.Length > 0 ? $"Unexpected column(s): {string.Join(", ", unknown)}. " : "") +
            (missing.Length > 0 ? $"Missing column(s): {string.Join(", ", missing)}. " : "") +
            "Teach this migration the new columns, or delete it if every deployment has already run it " +
            "(see the class docs).");
    }

    private static void Execute(System.Data.Common.DbConnection conn, string sql) => Execute(conn, null, sql);

    private static void Execute(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
