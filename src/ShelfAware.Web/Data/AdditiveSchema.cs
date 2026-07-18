using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// Post-v3 additive-only migrations. <c>EnsureCreated</c> builds the full schema on a fresh DB but
/// never alters an existing one, and the app deliberately has no migrations — so a column added
/// after v3 shipped is applied here as an idempotent <c>ALTER TABLE … ADD COLUMN</c> on startup.
/// Additive DEFAULT-valued columns only; anything structural is a fresh-DB change (see PantryDbGuard
/// and the v3 notes in CLAUDE.md).
///
/// Both DBs get the same treatment. auth.db was described as "a fresh file per deployment site", which
/// stopped being true the moment a deployment had accounts in it worth keeping — an added column there
/// needs the same ALTER as the pantry's, or the next query fails on a real user's live database.
/// </summary>
public static class AdditiveSchema
{
    public static void Apply(ShelfAwareDbContext db)
    {
        // 2026-07-12: the user's "I checked every line" flag for the in-app accuracy check.
        EnsureColumn(db, table: "Receipts", column: "VerifiedForEval", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-07-17: flavor/varietal as per-purchase metadata (like Brand and Size) — the Variety
        // feature. Pre-existing rows get NULL: their variety, if any, is baked into the product name.
        EnsureColumn(db, table: "ReceiptLines", column: "Variety", definition: "TEXT NULL");
        EnsureColumn(db, table: "PurchaseEvents", column: "Variety", definition: "TEXT NULL");

        // 2026-07-18: human-entered expiration dates as per-purchase metadata — the expiration-tracking
        // feature (opt-in per household via SettingKeys.TrackExpirationDates). NULL = no date recorded.
        EnsureColumn(db, table: "ReceiptLines", column: "ExpirationDate", definition: "TEXT NULL");
        EnsureColumn(db, table: "PurchaseEvents", column: "ExpirationDate", definition: "TEXT NULL");
    }

    public static void Apply(AuthDbContext db)
    {
        // 2026-07-15: invite codes stopped being permanent, unlimited bearer credentials. Existing codes
        // get NULL/0 — no expiry, no use limit — which is exactly what they already were, so a live
        // deployment's outstanding invites keep working until someone regenerates them.
        EnsureColumn(db, table: "Households", column: "InviteExpiresAt", definition: "TEXT NULL");
        EnsureColumn(db, table: "Households", column: "InviteMaxUses", definition: "INTEGER NULL");
        EnsureColumn(db, table: "Households", column: "InviteUseCount", definition: "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(DbContext db, string table, string column, string definition)
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
