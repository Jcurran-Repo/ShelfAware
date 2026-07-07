using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Data;

/// <summary>Startup guard for the v3 breaking schema change. v3 (households) shipped WITHOUT an
/// upgrade path for pre-account pantry DBs — deliberate: receipts re-import, and the old per-column
/// additive migrations only served pre-v3 DBs and left with them. An old file would otherwise fail on
/// the first query with a confusing "no such column: HouseholdId".</summary>
public static class PantryDbGuard
{
    public static void ThrowIfPreHouseholdDb(ShelfAwareDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Products') " +
                "AND NOT EXISTS (SELECT 1 FROM pragma_table_info('Products') WHERE name = 'HouseholdId');";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            {
                throw new InvalidOperationException(
                    "This pantry database predates accounts & households (v3) and can't be upgraded in place. " +
                    "Back it up if you want the file, then delete shelfaware.db* from the data directory — " +
                    "a fresh schema is built on the next start and receipts can be re-imported. See CLAUDE.md.");
            }
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }
}
