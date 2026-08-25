// Snapshot one LIVE SQLite database file to a new file, consistently, without stopping the app.
//
//   usage: SqliteSnapshot <source.db> <dest.db>     (dest must not exist yet)
//
// Why VACUUM INTO and not File.Copy: under WAL, committed data lives in the -wal sidecar until a
// checkpoint folds it in, so copying the .db alone loses transactions and copying .db + -wal can
// tear both mid-write. VACUUM INTO reads the whole database inside ONE read transaction (WAL
// readers never block the app's writers) and writes a compacted, self-contained copy - no
// sidecars, no torn state, and the snapshot cannot see a half-written transaction.
//
// The source is opened READ-ONLY, so this tool is physically unable to modify the database it
// backs up. After the snapshot, PRAGMA integrity_check runs against the COPY - a backup nobody
// has ever opened is a hope, not a backup.
//
// Exit codes: 0 = snapshot written and the copy checks "ok"; 1 = snapshot or check failed;
// 2 = bad arguments. The calling script treats nonzero as a failed backup.
using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: SqliteSnapshot <source.db> <dest.db>");
    return 2;
}

var source = args[0];
var dest = args[1];
if (!File.Exists(source))
{
    Console.Error.WriteLine($"source not found: {source}");
    return 2;
}
if (File.Exists(dest))
{
    // VACUUM INTO refuses an existing target; saying so here names the actual problem instead of
    // surfacing a SQLite error about a "target file".
    Console.Error.WriteLine($"dest already exists: {dest}");
    return 2;
}

try
{
    // Pooling=false: this process must hold no cached handle once it exits, or the -wal/-shm it
    // pinned would linger until pool cleanup instead of closing with the connection.
    await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = source,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ConnectionString))
    {
        await db.OpenAsync();
        await using var vacuum = db.CreateCommand();
        vacuum.CommandText = "VACUUM INTO @dest";
        vacuum.Parameters.AddWithValue("@dest", dest);
        await vacuum.ExecuteNonQueryAsync();
    }

    await using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = dest,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ConnectionString))
    {
        await copy.OpenAsync();
        await using var check = copy.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        var verdict = (string?)await check.ExecuteScalarAsync();
        if (verdict != "ok")
        {
            Console.Error.WriteLine($"integrity_check on the copy said: {verdict}");
            return 1;
        }
    }

    Console.WriteLine($"ok {new FileInfo(dest).Length}");
    return 0;
}
catch (SqliteException ex)
{
    Console.Error.WriteLine($"snapshot failed: {ex.Message}");
    return 1;
}
