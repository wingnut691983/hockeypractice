using HockeyPractice.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HockeyPractice.Services;

public record RestoreCheck(bool Ok, string? Error = null);

/// <summary>
/// Takes a copy of the database, and puts an uploaded one back.
///
/// Kept apart from the controller because the sharp edges are all here: what makes a copy
/// trustworthy, what makes an uploaded file safe to accept, and the exact order the files have to
/// move in so a failure halfway through still leaves a working site.
/// </summary>
public class DatabaseBackupService
{
    /// <summary>
    /// The 16 bytes every SQLite file begins with, trailing NUL included. Checked so a wrong
    /// file gets a plain answer here rather than an opaque driver error three steps later.
    /// </summary>
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    private readonly DataPaths _paths;
    private readonly ILogger<DatabaseBackupService> _log;

    public DatabaseBackupService(DataPaths paths, ILogger<DatabaseBackupService> log)
    {
        _paths = paths;
        _log = log;
    }

    /// <summary>The database that was live before the most recent restore, kept as the undo.</summary>
    public string ReplacedDatabase => _paths.Database + ".replaced";

    public long DatabaseBytes => Size(_paths.Database);
    public long ReplacedBytes => Size(ReplacedDatabase);
    public DateTime? ReplacedAtUtc =>
        File.Exists(ReplacedDatabase) ? File.GetLastWriteTimeUtc(ReplacedDatabase) : null;

    /// <summary>
    /// Writes a consistent copy of the live database to a fresh file and returns its path. The
    /// caller owns that file and must delete it.
    ///
    /// VACUUM INTO does the copy from inside a read transaction, so the result is coherent even
    /// if someone saves a plan while it runs, and it arrives as a single tidy file with no
    /// write-ahead log beside it. Copying the file with the filesystem is what you must not do:
    /// that can catch it mid-write and produce a backup that will not open, which is the worst
    /// possible outcome for a backup because you find out only when you need it.
    /// </summary>
    public async Task<string> SnapshotAsync(CancellationToken ct = default)
    {
        // Alongside the live database on purpose: VACUUM INTO cannot write across a filesystem
        // boundary any more cheaply, and keeping it here means the size counts against the same
        // quota the caller checked.
        var target = Path.Combine(_paths.Root, $"snapshot-{Guid.NewGuid():N}.db");

        await using var connection = new SqliteConnection(_paths.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        await command.ExecuteNonQueryAsync(ct);

        return target;
    }

    /// <summary>
    /// Decides whether an uploaded file can safely become the live database.
    ///
    /// <paramref name="knownMigrations"/> is every schema change this build understands. Anything
    /// in the file that is not in that list means the backup came from a newer version of the
    /// site, and startup only ever migrates forward, so it would be loaded against a schema this
    /// code cannot reason about. A file missing some of them is fine: those get applied on the
    /// restart that follows.
    /// </summary>
    public async Task<RestoreCheck> ValidateAsync(string path, IEnumerable<string> knownMigrations,
        CancellationToken ct = default)
    {
        await using (var probe = File.OpenRead(path))
        {
            var header = new byte[SqliteMagic.Length];
            if (await probe.ReadAsync(header, ct) < header.Length || !header.SequenceEqual(SqliteMagic))
                return new RestoreCheck(false, "That file is not a SQLite database.");
        }

        try
        {
            // Read only, so a check can never be the thing that damages the file being checked.
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await connection.OpenAsync(ct);

            await using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check";
                var result = await integrity.ExecuteScalarAsync(ct) as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("Rejected a restore: integrity_check said {Result}", result);
                    return new RestoreCheck(false,
                        "That database file is damaged, so loading it would lose data rather than restore it.");
                }
            }

            var applied = new List<string>();
            await using (var history = connection.CreateCommand())
            {
                history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
                try
                {
                    await using var reader = await history.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
                }
                catch (SqliteException)
                {
                    return new RestoreCheck(false,
                        "That is a SQLite database, but not one of this site's backups.");
                }
            }

            if (applied.Count == 0)
                return new RestoreCheck(false,
                    "That database has no schema history, so it is not one of this site's backups.");

            var unknown = applied.Except(knownMigrations, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                _log.LogWarning("Rejected a restore carrying {Count} unknown migration(s), first {First}",
                    unknown.Count, unknown[0]);
                return new RestoreCheck(false,
                    "That backup came from a newer version of the site than the one running now. " +
                    "Deploy that version first, then restore.");
            }

            return new RestoreCheck(true);
        }
        catch (SqliteException ex)
        {
            _log.LogWarning("Rejected a restore that would not open: {Error}", ex.Message);
            return new RestoreCheck(false, "That file could not be opened as a database.");
        }
    }

    /// <summary>
    /// Makes <paramref name="incoming"/> the live database, keeping the current one as the undo.
    /// The incoming file is consumed. Validate first: this does not check anything.
    ///
    /// Moves rather than copies, so each step is atomic and there is no window where the live
    /// path holds a half-written file.
    /// </summary>
    public void Swap(string incoming)
    {
        var live = _paths.Database;

        // Fold the write-ahead log back into the database file FIRST, while it is still the live
        // one. This is what makes the kept copy trustworthy: in WAL mode the newest transactions
        // live in the sidecar, so moving the .db on its own would quietly hand back an undo file
        // that is missing the most recent writes. Checkpointing first leaves everything in one
        // file, and the sidecars with nothing left to lose.
        Checkpoint();

        // Pooled connections still point at the file about to be moved aside. On Linux the moves
        // would succeed regardless, but a pooled handle would keep reading the old data until the
        // process restarted, which is exactly the confusing half-state worth spending a line on.
        SqliteConnection.ClearAllPools();

        // The sidecars go with it. Anything that ever opened the previous kept copy could have
        // left a write-ahead log beside it, and leaving that to be inherited by the NEXT kept
        // copy would quietly corrupt the one file the whole restore relies on being readable.
        if (File.Exists(ReplacedDatabase)) File.Delete(ReplacedDatabase);
        DeleteSidecars(ReplacedDatabase);

        var movedAside = File.Exists(live);
        if (movedAside) File.Move(live, ReplacedDatabase);

        // Whatever is left of the sidecars belongs to the database that has just been moved away.
        // Left in place, SQLite would take them for the restored file's own and replay them into
        // it, which corrupts it. They go before anything takes their place.
        DeleteSidecars(live);

        try
        {
            File.Move(incoming, live);
        }
        catch (IOException)
        {
            // There is a moment here with no database at the live path at all, and a failure
            // inside it would leave the site with nothing rather than with old data. Two renames
            // in one directory should not fail, but "should not" is doing a lot of work when the
            // cost of being wrong is the whole site. Put the old one back and rethrow.
            if (movedAside && !File.Exists(live)) File.Move(ReplacedDatabase, live);
            throw;
        }

        _log.LogWarning("Database replaced from an uploaded backup. Previous file kept at {Path}",
            ReplacedDatabase);
    }

    /// <summary>
    /// Writes any write-ahead log back into the database file and truncates it. A no-op on a
    /// database that is not in WAL mode, and never worth failing a restore over: the worst case
    /// is the kept copy needing its own sidecar to be complete, which is still recoverable.
    /// </summary>
    private void Checkpoint()
    {
        try
        {
            using var connection = new SqliteConnection(_paths.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            _log.LogWarning("Could not checkpoint before the swap: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Removes the write-ahead log and shared-memory files SQLite names after a database. Both
    /// are derived from the database's own filename, so a sidecar left next to a path that later
    /// holds a DIFFERENT database gets replayed into it. Always clear them alongside the file
    /// they belong to.
    /// </summary>
    private static void DeleteSidecars(string database)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = database + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
