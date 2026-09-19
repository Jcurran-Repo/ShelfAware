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
/// than taken on trust. It also keeps a migrated database's COLUMN SET the same as a fresh one's, so no
/// later read has to know which kind of database it is on. Not a byte-for-byte schema match: the ALTER
/// writes <c>AmountCredits INTEGER NOT NULL DEFAULT 0</c> (a default is the only way SQLite will add a NOT
/// NULL column to a populated table) where EnsureCreated emits it without one, and nothing compares a
/// migrated CreditLedger against a fresh one. The AdditiveSchema parity tests cover the tables
/// <see cref="AdditiveSchema"/> creates — they do not reach this migration.</para>
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

            // ⚠️ Refuse rather than convert at a nonsense rate. RetailMicrosPerCredit CLAMPS to 1 so that
            // ordinary arithmetic stays defined, which is right everywhere except here: this runs ONCE and
            // cannot be undone, so at that clamp it divides by one micro and turns a $1.65 welcome grant
            // into 1,650,000 credits, permanently. Program.cs validates the anchor at boot so this should be
            // unreachable — but "unreachable" is a claim about today's startup code, and the cost of being
            // wrong about it is a ledger nobody can put back.
            // ⚠️ The test is the COMPUTED divisor, not the two inputs. An earlier version checked that both
            // the anchor and the markup were positive, which is a narrower statement than the comment above
            // it: an anchor of 0.0000001 is positive, passes every boot check, and still clamps the divisor
            // to 1. What matters is whether a credit has a real retail price, so that is what is asked.
            var retailMicrosPerCredit = CreditPricing.RetailMicrosPerCredit(billing);
            if (retailMicrosPerCredit <= 1)
                throw new InvalidOperationException(
                    "Refusing to re-denominate the credit ledger: Billing:CostDollarsPerCredit × " +
                    "Billing:CreditMarkup gives a credit a retail price of one micro or less, so every " +
                    "balance would convert to roughly a million times itself. The conversion is one-shot " +
                    "and irreversible, so it will not run at a rate that cannot be right.");

            using var tx = conn.BeginTransaction();
            Execute(conn, tx, "ALTER TABLE CreditLedger ADD COLUMN AmountCredits INTEGER NOT NULL DEFAULT 0;");
            // ROUND(x) in SQLite rounds half away from zero — a balance somebody already holds should
            // convert to the NEAREST whole credit, not be floored down. Signed throughout, so a consumption
            // or an expiry converts to a negative credit count exactly as it should.
            // ⚠️ PER ROW, and a sum of rounded rows is NOT the rounded sum — accepted deliberately
            // (Jordan, 2026-09-19), and stated here because the paragraph above reads as though rounding to
            // nearest settles the question and it does not. Every movement under half a credit converts to
            // ZERO, and pre-credit charges were cost-denominated, so most historical SPEND lands under that
            // line while a grant at a round dollar figure converts exactly: the residual is one-directional
            // and in the household's favour, not a random walk. Held because the exposure is bounded to the
            // databases that already exist — a fresh box has both columns and returns above, so this can
            // only ever run against those — and both are free-use today, where a consumption row needs
            // managed billing AND configured payments AND a non-unlimited tier to have been written at all.
            // LegacyAmountMicros is kept, so a household's true balance stays recomputable if that is ever
            // wrong: the receipt is what makes accepting this reversible. Getting the balance exact would
            // mean one adjustment entry per household for the residual.
            // ⚠️ This SQL is the ONE statement of the retail→credit rule. There used to be a C# twin
            // (CreditPricing.CreditsFromRetailMicros) that nothing but tests called, which is two definitions
            // of one rule with a test standing between them; the rounding table is now asserted through this
            // statement in CreditDenominationMigrationTests, against the SQLite that actually runs it.
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
