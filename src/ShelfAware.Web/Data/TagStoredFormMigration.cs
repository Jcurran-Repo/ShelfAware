using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Tagging;

namespace ShelfAware.Web.Data;

/// <summary>
/// The one-off normalize-and-rewrite pass over the tag columns (2026-10-07): every stored tag is put in
/// <see cref="TagVocabulary.StoredForm"/> — trimmed, whitespace runs collapsed, NFC — and two rows on
/// one owner that then read the same keep the earlier and lose the later.
///
/// <para>⚠️ Why a row can need it. <c>FindNearDuplicate</c> measures each vocabulary entry RAW before it
/// pays to key it — deliberately, since the cost it bounds is the cost of normalizing at all — and a tag
/// written before the cap existed could be over the cap raw and inside it keyed: 33 decomposed "é" is 66
/// characters raw and 33 composed, and "a", a run of spaces, "b" is as long as the run. Such a row was
/// skipped, so that household's dedup stopped seeing one of its own tags and the cloud could fragment
/// on a difference nobody can see — the exact thing the Unicode fold was added to prevent.
/// <c>Canonicalize</c> now writes the stored form, so only rows written before it did can be in this
/// state; this pass brings them across once.</para>
///
/// <para>⚠️ Cross-household by nature, like its siblings: it runs at boot, outside any household scope,
/// on the raw connection — not through a fifth <c>IgnoreQueryFilters</c> site (the ones that exist are
/// counted and gated in docs/architecture.md). It never reads a row's owner except to keep two
/// households' tags apart: a collision is judged within one (household, product-or-recipe), so one
/// household's duplicate can never cost another its identically-spelled tag.</para>
///
/// <para>A row that is over the cap in EVERY form (<see cref="TagVocabulary.IsOverLengthInAnyForm"/>, or
/// still over it once stored) is not a tag and never was; it is left exactly as it is and counted, so the
/// log says it is there, rather than normalized at a cost the cap exists to avoid or deleted on a boot
/// pass's own authority. Idempotent: a second boot finds every row already in stored form and nothing
/// to collapse, and does nothing. One transaction per boot, so a crash mid-way leaves the columns as
/// they were rather than half-converted.</para>
///
/// <para>⚠️ Runs STRICTLY AFTER <see cref="AdditiveSchema.Apply(ShelfAwareDbContext)"/>, which is what
/// creates <c>RecipeTags</c> at all on a database that predates it.</para>
/// </summary>
public static class TagStoredFormMigration
{
    /// <summary>What one boot did: rows rewritten into stored form, later duplicates deleted, and rows
    /// left alone because they are over the cap in every form.</summary>
    public readonly record struct Outcome(int Rewritten, int Collapsed, int LeftAlone);

    /// <summary>Every tag-bearing table and the column that names a row's owner. ⚠️ A new tag table goes
    /// here or its pre-cap rows stay invisible to dedup; the names are this file's own constants, which is
    /// what lets them be spliced into SQL.</summary>
    private static readonly (string Table, string OwnerColumn)[] TagTables =
    [
        ("ProductTags", "ProductId"),
        ("RecipeTags", "RecipeId"),
    ];

    private sealed record Row(long Id, string Household, long Owner, string Value);

    public static Outcome Apply(ShelfAwareDbContext db, ILogger logger)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var tx = conn.BeginTransaction();
            int rewritten = 0, collapsed = 0, leftAlone = 0;
            foreach (var (table, ownerColumn) in TagTables)
            {
                if (!TableExists(conn, tx, table)) continue;
                var (r, c, l) = Rewrite(conn, tx, table, ownerColumn);
                rewritten += r;
                collapsed += c;
                leftAlone += l;
            }
            tx.Commit();

            var outcome = new Outcome(rewritten, collapsed, leftAlone);
            if (rewritten > 0 || collapsed > 0)
                logger.LogInformation(
                    "Tag stored-form pass: rewrote {Rewritten} tag(s) into stored form and collapsed {Collapsed} duplicate(s) that then read the same.",
                    rewritten, collapsed);
            if (leftAlone > 0)
                logger.LogWarning(
                    "{Count} stored tag(s) are over the tag cap in every form and were left alone; they were never tags and the dedup does not see them.",
                    leftAlone);
            return outcome;
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>One table: read every row in id order, decide each against the rows before it on the same
    /// owner, and write the decisions back. Rows are read whole before anything is written, so the reader
    /// is closed by the time the first UPDATE runs.</summary>
    private static (int Rewritten, int Collapsed, int LeftAlone) Rewrite(
        DbConnection conn, DbTransaction tx, string table, string ownerColumn)
    {
        int rewritten = 0, collapsed = 0, leftAlone = 0;
        // What each owner already carries, in stored form, compared the way Canonicalize asks "does the
        // product already carry this tag" — case-insensitively — so the pass agrees with the write path on
        // what a duplicate is. Near-duplicates ("Snack" beside "Snacks") are NOT collapsed: that is the
        // dedup's call to make at write time, with the household able to override it, not a boot pass's.
        var carried = new Dictionary<(string Household, long Owner), HashSet<string>>();
        foreach (var row in Read(conn, tx, table, ownerColumn))
        {
            // ⚠️ The linear check BEFORE the normal form, so a 4 MB legacy row is refused rather than
            // normalized — the quadratic cost the cap exists to keep off this path.
            if (TagVocabulary.IsOverLengthInAnyForm(row.Value)) { leftAlone++; continue; }
            var stored = TagVocabulary.StoredForm(row.Value);
            if (TagVocabulary.IsOverLength(stored)) { leftAlone++; continue; }

            if (!carried.TryGetValue((row.Household, row.Owner), out var tags))
                carried[(row.Household, row.Owner)] = tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!tags.Add(stored))
            {
                Execute(conn, tx, $"DELETE FROM \"{table}\" WHERE \"Id\" = @id;", ("@id", row.Id));
                collapsed++;
                continue;
            }
            if (stored == row.Value) continue; // already in stored form — the every-later-boot case
            Execute(conn, tx, $"UPDATE \"{table}\" SET \"Value\" = @value WHERE \"Id\" = @id;", ("@value", stored), ("@id", row.Id));
            rewritten++;
        }
        return (rewritten, collapsed, leftAlone);
    }

    private static List<Row> Read(DbConnection conn, DbTransaction tx, string table, string ownerColumn)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT \"Id\", \"HouseholdId\", \"{ownerColumn}\", \"Value\" FROM \"{table}\" ORDER BY \"Id\";";
        var rows = new List<Row>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // A pre-household row has no owner id; PantryDbGuard refuses such a database at boot, so this
            // is belt-and-braces — but an ownerless row still keys to one bucket rather than throwing.
            var household = reader.IsDBNull(1) ? "" : reader.GetString(1);
            rows.Add(new Row(reader.GetInt64(0), household, reader.GetInt64(2), reader.GetString(3)));
        }
        return rows;
    }

    private static bool TableExists(DbConnection conn, DbTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
        AddParameter(cmd, "@name", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void Execute(DbConnection conn, DbTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) AddParameter(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
