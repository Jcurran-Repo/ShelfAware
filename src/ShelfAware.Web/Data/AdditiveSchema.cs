using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Data;

/// <summary>
/// Post-v3 additive-only migrations. <c>EnsureCreated</c> builds the full schema on a fresh DB but
/// never alters an existing one, and the app deliberately has no migrations — so a column added
/// after v3 shipped is applied here as an idempotent <c>ALTER TABLE … ADD COLUMN</c> on startup.
/// Additive DEFAULT-valued columns only; anything structural is a fresh-DB change (see PantryDbGuard
/// and the v3 notes in CLAUDE.md).
/// </summary>
public static class AdditiveSchema
{
    public static void Apply(ShelfAwareDbContext db)
    {
        // 2026-07-12: the user's "I checked every line" flag for the in-app accuracy check.
        EnsureColumn(db, table: "Receipts", column: "VerifiedForEval", definition: "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(ShelfAwareDbContext db, string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                $"SELECT EXISTS (SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}');";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }
}
