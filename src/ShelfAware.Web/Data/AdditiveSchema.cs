using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// Post-v3 additive-only migrations. <c>EnsureCreated</c> builds the full schema on a fresh DB but
/// never alters an existing one, and the app deliberately has no migrations — so a column added
/// after v3 shipped is applied here as an idempotent <c>ALTER TABLE … ADD COLUMN</c> on startup,
/// and a table added after v3 as an idempotent CREATE (whose DDL comes from EF's own create script,
/// so the migrated file and a fresh file cannot drift apart — pinned by AdditiveSchemaTests).
/// Additive only — DEFAULT-valued columns and whole new tables, both of which existing rows never
/// notice; anything that changes existing data's shape is a fresh-DB change (see PantryDbGuard and
/// the v3 notes in CLAUDE.md; NullableInviteCodeMigration is the one documented exception).
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

        // 2026-07-18: the meal log — "Ate it" started recording WHEN, for the Reports tab's
        // meals/calories-over-time charts. A brand-new table is invisible to existing rows.
        EnsureTable(db, table: "MealEvents");

        // 2026-07-18: named report configurations (the Reports tab's "Save this report").
        EnsureTable(db, table: "SavedReports");

        // 2026-07-22: which receipt's confirm created the product / taught the alias — provenance
        // for "remove this receipt". Pre-existing rows get NULL (unknown origin), which removal
        // reads as "keep": it only ever deletes what it can prove the receipt did.
        EnsureColumn(db, table: "Products", column: "CreatedByReceiptId", definition: "INTEGER NULL");
        EnsureColumn(db, table: "ProductAliases", column: "TaughtByReceiptId", definition: "INTEGER NULL");
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

    /// <summary>Create <paramref name="table"/> (and its indexes) on a DB built before it existed. The
    /// DDL is not hand-written: it's lifted from <c>GenerateCreateScript()</c> — the exact statements
    /// EnsureCreated runs on a fresh file — so there is no second copy of the schema to keep honest.
    /// A model change to the table automatically changes what gets created here.</summary>
    private static void EnsureTable(DbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
            var nameParam = check.CreateParameter();
            nameParam.ParameterName = "@name";
            nameParam.Value = table;
            check.Parameters.Add(nameParam);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            foreach (var statement in StatementsFor(db, table))
            {
                using var create = conn.CreateCommand();
                create.CommandText = statement;
                create.ExecuteNonQuery();
            }
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>The create-script statements that build <paramref name="table"/>: its CREATE TABLE and
    /// every CREATE INDEX on it. Splitting the script on ";" is safe because this schema's DDL contains
    /// no embedded semicolons (identifiers and defaults are plain; nothing user-supplied is in the
    /// script) — and the schema-parity test would fail the moment a model change broke that assumption,
    /// because the mangled statement wouldn't rebuild the fresh schema.</summary>
    private static IEnumerable<string> StatementsFor(DbContext db, string table)
    {
        var script = db.Database.GenerateCreateScript();
        var statements = script
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
        foreach (var statement in statements)
        {
            if (statement.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal) ||
                (statement.StartsWith("CREATE ", StringComparison.Ordinal) &&
                 statement.Contains($" ON \"{table}\" ", StringComparison.Ordinal)))
            {
                yield return statement;
            }
        }
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
