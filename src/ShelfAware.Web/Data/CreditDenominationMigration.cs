using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Billing;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// The one-time re-denomination of the credit ledger from RETAIL MICROS to Shelf Aware CREDITS
/// (2026-09-19, docs/remediation-plan.md §7). Adds <c>AmountCredits</c> to <c>CreditLedger</c> and converts
/// every existing row at the anchor, in ONE transaction.
///
/// <para>⚠️ This is a MONEY migration, and the transaction is why it is a class of its own rather than an
/// <see cref="AdditiveSchema"/> line. Adding the column and converting the rows must commit TOGETHER: if the
/// process died between them, the next boot would see the column present, skip the conversion it keys on the
/// column's ABSENCE, and every pre-existing balance would silently read as zero. SQLite's DDL is
/// transactional, so the ALTER and the UPDATE land or roll back as one and the "column absent" guard stays
/// an exact statement of "not yet converted". That is also why <c>AmountCredits</c> is deliberately NOT in
/// <see cref="AdditiveSchema.Apply"/> — an additive pass that added it first would disarm this guard.</para>
///
/// <para>The old <c>AmountMicros</c> column is KEPT, mapped to <see cref="CreditLedgerEntry.LegacyAmountMicros"/>
/// — the receipt for this conversion, so the arithmetic behind every converted balance can be audited rather
/// than taken on trust. It also keeps a migrated schema identical to a freshly created one, which the
/// AdditiveSchema parity tests pin.</para>
///
/// <para>Idempotent (the column exists → nothing to do) and a no-op on a fresh database, where
/// <c>EnsureCreated</c> already built the table with both columns and there are no rows to convert.
/// Deletable once every deployment has booted past this.</para>
///
/// <para>⚠️ Runs STRICTLY AFTER <see cref="AdditiveSchema.Apply"/>, which is what creates the
/// <c>CreditLedger</c> table at all on a database that predates it.</para>
/// </summary>
public static class CreditDenominationMigration
{
    public static void Apply(AuthDbContext db, BillingOptions billing)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            if (!TableExists(conn, "CreditLedger")) return;
            if (ColumnExists(conn, "CreditLedger", "AmountCredits")) return; // already converted

            var retailMicrosPerCredit = CreditPricing.RetailMicrosPerCredit(billing);

            using var tx = conn.BeginTransaction();
            Execute(conn, tx, "ALTER TABLE CreditLedger ADD COLUMN AmountCredits INTEGER NOT NULL DEFAULT 0;");
            // ROUND(x) in SQLite rounds half away from zero, matching CreditPricing.CreditsFromRetailMicros
            // — the same reading of an existing balance a C# conversion would give. Signed throughout, so a
            // consumption or an expiry converts to a negative credit count exactly as it should.
            Execute(conn, tx,
                $"UPDATE CreditLedger SET AmountCredits = CAST(ROUND(CAST(AmountMicros AS REAL) / {retailMicrosPerCredit}) AS INTEGER);");
            tx.Commit();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    private static bool TableExists(System.Data.Common.DbConnection conn, string table) =>
        Scalar(conn, "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);", ("@name", table)) > 0;

    private static bool ColumnExists(System.Data.Common.DbConnection conn, string table, string column) =>
        Scalar(conn, $"SELECT EXISTS (SELECT 1 FROM pragma_table_info('{table}') WHERE name = @name);", ("@name", column)) > 0;

    private static long Scalar(System.Data.Common.DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Execute(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
