using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using HockeyPractice.Infrastructure;
using Microsoft.Extensions.Options;

namespace HockeyPractice.Services;

/// <summary>
/// What one archive says about itself. Written into the zip so a restore can refuse a file it
/// does not understand instead of half-applying it.
/// </summary>
public class BackupManifest
{
    /// <summary>
    /// Bumped when the archive LAYOUT changes, not when the app does. A restore refuses anything
    /// newer than it knows, for the same reason the database restore refuses unknown migrations:
    /// a newer archive may hold entries this build would silently ignore.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedUtc { get; set; }
    public List<string> Migrations { get; set; } = new();
    public long DbBytes { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public record BackupResult(string Path, long Bytes, int FileCount);

/// <summary>
/// Packs the whole persistent volume into one zip.
///
/// The database download that already exists covers one file out of three kinds of durable state.
/// File paths are keyed on database IDs, so the database and the uploaded files only make sense
/// together — restoring one without the other gives you rows pointing at nothing. That is the
/// whole argument for archiving the volume rather than the database.
/// </summary>
public class VolumeBackupService
{
    /// <summary>The newest archive layout this build can write and read.</summary>
    public const int SchemaVersion = 1;

    public const string ManifestEntry = "manifest.json";
    public const string DatabaseEntry = "hockeypractice.db";

    /// <summary>
    /// Seconds are in the name deliberately. With minute precision, pressing "Archive now" twice
    /// inside one minute writes the same key twice and you quietly hold fewer copies than the
    /// page says you do.
    /// </summary>
    private const string StampFormat = "yyyy-MM-dd-HHmmss";

    /// <summary>
    /// The one definition of what an archive is called. Retention, the admin list and the restore
    /// picker all filter on this, so nothing else in the bucket can ever be listed, offered or
    /// deleted by this app — that is what makes it safe to point local testing at the same bucket
    /// under a different prefix.
    /// </summary>
    public static readonly Regex NamePattern =
        new(@"^hockeypractice-\d{4}-\d{2}-\d{2}-\d{6}\.zip$", RegexOptions.Compiled);

    public static string NameFor(DateTime utc) =>
        $"hockeypractice-{utc.ToString(StampFormat, CultureInfo.InvariantCulture)}.zip";

    /// <summary>
    /// Above this, a file is streamed straight into the zip instead of being read into memory for
    /// the stability check below. Plan PDFs cap at 15 MB and diagrams at 10 MB, so nothing should
    /// reach it; it exists so an unexpected file cannot take the container down with an OOM.
    /// </summary>
    private const long StreamDirectlyAbove = 64L * 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly DatabaseBackupService _database;
    private readonly VolumeBackupOptions _options;
    private readonly ILogger<VolumeBackupService> _log;

    public VolumeBackupService(DataPaths paths, DatabaseBackupService database,
        IOptions<VolumeBackupOptions> options, ILogger<VolumeBackupService> log)
    {
        _paths = paths;
        _database = database;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Writes an archive of the whole volume to <paramref name="targetPath"/>. The caller owns
    /// that file and must delete it.
    ///
    /// <paramref name="migrations"/> is the schema history to record in the manifest; it comes
    /// from a scope, because the caller is a singleton.
    /// </summary>
    public async Task<BackupResult> CreateAsync(string targetPath, IEnumerable<string> migrations,
        CancellationToken ct = default)
    {
        // First half of the size fuse. The zip is smaller than the volume, so this cannot be the
        // only check, but it is the one that refuses before spending the time and the disk.
        var used = _paths.UsedBytes();
        if (_options.MaxBytes > 0 && used > _options.MaxBytes)
        {
            throw new InvalidOperationException(
                $"The volume holds {PlanStorageService.Human(used)}, over the " +
                $"{PlanStorageService.Human(_options.MaxBytes)} archive limit.");
        }

        // Staged beside the zip rather than on the volume: a nightly backup must not need free
        // space on the disk it is protecting. A restore is the opposite case and stages on the
        // volume on purpose — see RestoreBackup.
        var stagingDirectory = Path.GetDirectoryName(targetPath)!;
        var snapshot = await _database.SnapshotAsync(stagingDirectory, ct);

        try
        {
            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, useAsync: true);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            // The database goes in FIRST, and the file tree second, deliberately. A plan created
            // while this runs then has files with no row pointing at them, which is an invisible
            // orphan. The other order gives a row with no file, which renders as a broken plan
            // for every family on the team.
            var dbBytes = new FileInfo(snapshot).Length;
            await AddFileAsync(zip, DatabaseEntry, snapshot, ct);

            var fileCount = 0;
            long totalBytes = 0;

            // An ALLOWLIST, not a list of exclusions. Getting this wrong by omission would mean
            // shipping a transient file (a -journal, a half-written restore staging copy, the
            // .replaced undo) into the archive, and one of those extracted next to the restored
            // database corrupts it. An exclusion list has to be kept in step with every new
            // transient name; this cannot leak one.
            foreach (var root in new[] { _paths.KeyRing, _paths.TeamsRoot })
            {
                if (!Directory.Exists(root)) continue;

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var entry = EntryNameFor(file);
                    var bytes = await AddFileAsync(zip, entry, file, ct);
                    fileCount++;
                    totalBytes += bytes;

                    // Second half of the fuse, checked as it grows. The final size is not knowable
                    // in advance, so a limit tested only at the end is no limit at all: the pod
                    // would already have been evicted for running the ephemeral disk out.
                    if (_options.MaxBytes > 0 && output.Length > _options.MaxBytes)
                    {
                        throw new InvalidOperationException(
                            $"The archive passed {PlanStorageService.Human(_options.MaxBytes)} " +
                            "while it was being written, so it was abandoned.");
                    }
                }
            }

            var manifest = new BackupManifest
            {
                SchemaVersion = SchemaVersion,
                CreatedUtc = DateTime.UtcNow,
                Migrations = migrations.ToList(),
                DbBytes = dbBytes,
                FileCount = fileCount,
                TotalBytes = totalBytes
            };

            var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(manifestStream, manifest,
                    new JsonSerializerOptions { WriteIndented = true }, ct);

            zip.Dispose();
            await output.FlushAsync(ct);

            var size = output.Length;
            _log.LogInformation("Built a volume archive: {Bytes} bytes, {Files} files, db {DbBytes}",
                size, fileCount, dbBytes);

            return new BackupResult(targetPath, size, fileCount);
        }
        catch
        {
            // A half-written zip is worse than none: it would upload, look like a backup, and
            // fail only when someone needed it.
            TryDelete(targetPath);
            throw;
        }
        finally
        {
            TryDelete(snapshot);
        }
    }

    /// <summary>
    /// Whether an archive can safely be restored, and what it would cost to do it.
    /// </summary>
    /// <param name="StagedDatabase">
    /// The database entry, already extracted onto the volume beside the live one, ready to hand
    /// to <see cref="DatabaseBackupService.Swap"/>. The caller owns it and must delete it if the
    /// restore does not go ahead. Null when the check failed.
    /// </param>
    /// <param name="AdditionalBytes">
    /// How much the volume would actually grow: the database, plus the entries whose target does
    /// not already exist. Files that overwrite something cost nothing, and counting them would
    /// refuse a restore onto a nearly-full volume for space it is not going to use.
    /// </param>
    public record ArchiveCheck(
        bool Ok,
        string? Error = null,
        string? StagedDatabase = null,
        long AdditionalBytes = 0,
        BackupManifest? Manifest = null);

    /// <summary>
    /// Decides whether an archive can become the live site.
    ///
    /// Everything that can be checked without changing anything is checked here, because the
    /// caller's next step moves real files around.
    /// </summary>
    public async Task<ArchiveCheck> ValidateAsync(string zipPath, IEnumerable<string> knownMigrations,
        CancellationToken ct = default)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            return new ArchiveCheck(false, "That file is not a zip archive.");
        }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry is null)
                return new ArchiveCheck(false,
                    "That zip has no manifest, so it is not one of this site's backups.");

            BackupManifest? manifest;
            try
            {
                await using var manifestStream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream,
                    cancellationToken: ct);
            }
            catch (JsonException)
            {
                return new ArchiveCheck(false, "That backup's manifest could not be read.");
            }

            if (manifest is null)
                return new ArchiveCheck(false, "That backup's manifest is empty.");

            // Same reasoning as the migration check below, one level up: a newer LAYOUT may hold
            // entries this build would silently ignore, and a restore that quietly drops half an
            // archive is worse than one that refuses.
            if (manifest.SchemaVersion > SchemaVersion)
            {
                _log.LogWarning("Rejected an archive with schemaVersion {Version}, this build " +
                                "understands {Known}", manifest.SchemaVersion, SchemaVersion);
                return new ArchiveCheck(false,
                    "That backup came from a newer version of the site than the one running now. " +
                    "Deploy that version first, then restore.");
            }

            if (zip.GetEntry(DatabaseEntry) is not { } databaseEntry)
                return new ArchiveCheck(false, "That backup has no database in it.");

            // Every entry, before anything is written. A crafted zip can carry "../" or an
            // absolute path and land a file anywhere the process can write; the whole archive is
            // refused rather than the offending entry skipped, because an archive containing one
            // is not a backup this site produced.
            long additional = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;

                if (ResolveEntry(entry.FullName) is not { } target)
                {
                    _log.LogWarning("Rejected an archive: entry {Entry} escapes the volume",
                        entry.FullName);
                    return new ArchiveCheck(false,
                        "That backup contains a file path that points outside the site's storage, " +
                        "so it was refused.");
                }

                if (entry.FullName is ManifestEntry or DatabaseEntry) continue;
                if (!File.Exists(target)) additional += entry.Length;
            }

            // Onto the VOLUME, not temp. Swap moves this into place, and File.Move is only atomic
            // within one filesystem; from the container's ephemeral disk it becomes a copy that
            // can fail half-written and cannot be rolled back. See Swap's own guard.
            var staged = Path.Combine(_paths.Root, $"restore-{Guid.NewGuid():N}.db");
            try
            {
                databaseEntry.ExtractToFile(staged, overwrite: true);

                var dbCheck = await _database.ValidateAsync(staged, knownMigrations, ct);
                if (!dbCheck.Ok)
                {
                    CleanUpStagedDatabase(staged);
                    return new ArchiveCheck(false, dbCheck.Error);
                }

                return new ArchiveCheck(true, StagedDatabase: staged,
                    AdditionalBytes: additional + new FileInfo(staged).Length, Manifest: manifest);
            }
            catch (Exception)
            {
                CleanUpStagedDatabase(staged);
                throw;
            }
        }
    }

    /// <summary>
    /// Puts the archive's files back, over the top of whatever is there.
    ///
    /// Nothing is ever removed. Moving the existing tree aside instead would drop every file
    /// uploaded since the archive was taken, and it is not needed: the restored database decides
    /// what is visible, and a file with no row pointing at it is already invisible. The cost is
    /// orphaned bytes, which the storage meter already counts.
    /// </summary>
    public void ApplyFiles(string zipPath, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.FullName.EndsWith('/')) continue;
            if (entry.FullName is ManifestEntry or DatabaseEntry) continue;

            // Re-resolved rather than trusted from validation, so the safety check and the write
            // cannot drift apart.
            if (ResolveEntry(entry.FullName) is not { } target)
                throw new InvalidOperationException($"Entry {entry.FullName} escapes the volume.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Written to a sibling name and renamed into place, never straight over the target.
            // Extracting directly truncates the existing file the instant it is opened, so a
            // failure part way through — the volume filling up is the obvious one — leaves a
            // gutted PDF where a complete one was. Measured: a 36-byte file became 5 bytes.
            // A rename within a directory is atomic, so the target is either the old file or the
            // new one and never a torn mixture. It also means a player who is midway through
            // downloading a plan keeps reading the file they opened.
            var incoming = target + ".incoming";
            try
            {
                entry.ExtractToFile(incoming, overwrite: true);
                File.Move(incoming, target, overwrite: true);
            }
            catch
            {
                TryDelete(incoming);
                throw;
            }
        }
    }

    /// <summary>
    /// Where an archive entry is allowed to land, or null if it tries to escape.
    ///
    /// Only the two roots the archive is built from are accepted, so an entry naming anything
    /// else on the volume — the live database, the undo copy, a staging file — is refused even
    /// though it resolves inside the root.
    /// </summary>
    private string? ResolveEntry(string entryName)
    {
        if (entryName.Length == 0) return null;
        if (entryName is ManifestEntry or DatabaseEntry)
            return Path.Combine(_paths.Root, entryName);

        var root = Path.GetFullPath(_paths.Root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(_paths.Root,
            entryName.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(root, StringComparison.Ordinal)) return null;

        var relative = full[root.Length..];
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        return first is "teams" or "dpkeys" ? full : null;
    }

    /// <summary>Removes a staged database and the sidecars validating it leaves behind.</summary>
    public void CleanUpStagedDatabase(string path)
    {
        TryDelete(path);
        try
        {
            DatabaseBackupService.DeleteSidecars(path);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not clear sidecars for {Path}: {Error}", path, ex.Message);
        }
    }

    /// <summary>
    /// Path inside the zip, relative to the volume root, always with forward slashes so an
    /// archive written anywhere restores anywhere.
    /// </summary>
    private string EntryNameFor(string fullPath) =>
        Path.GetRelativePath(_paths.Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Copies one file in, re-reading it once if it changed underneath us.
    ///
    /// The database snapshot is consistent because VACUUM INTO runs in a read transaction. The
    /// file tree has no such guarantee: a coach uploading a PDF at 03:00 is unlikely but a
    /// half-captured PDF is silent, and you would find out only on the restore. Pausing writes
    /// nightly would be a worse trade, because a crash mid-archive would leave the site read-only
    /// until the 30-minute maintenance window expired.
    /// </summary>
    private async Task<long> AddFileAsync(ZipArchive zip, string entryName, string path,
        CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var target = entry.Open();

        var info = new FileInfo(path);
        if (info.Length > StreamDirectlyAbove)
        {
            _log.LogWarning("Archiving {Entry} ({Bytes} bytes) without the stability check, " +
                            "because it is larger than the in-memory limit", entryName, info.Length);
            await using var big = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await big.CopyToAsync(target, ct);
            return info.Length;
        }

        byte[] bytes;
        for (var attempt = 1; ; attempt++)
        {
            var before = new FileInfo(path);
            bytes = await File.ReadAllBytesAsync(path, ct);
            var after = new FileInfo(path);

            if (before.Length == after.Length && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
                break;

            if (attempt >= 2)
            {
                // Kept rather than dropped. A file that is being rewritten right now is still
                // better in the archive than absent from it, and the log says which one it was.
                _log.LogWarning("{Entry} changed while it was being archived, twice. Keeping the " +
                                "second read.", entryName);
                break;
            }
        }

        await target.WriteAsync(bytes, ct);
        return bytes.LongLength;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not clean up {Path}: {Error}", path, ex.Message);
        }
    }
}

/// <summary>
/// Tunables for the nightly archive. Sits at the bottom of its own service the way
/// <see cref="SiteOptions"/> sits at the bottom of <see cref="PlanStorageService"/>.
///
/// These are code decisions rather than deployment config, so they live in appsettings under
/// "Archive" instead of burning environment slots. Only the five values that genuinely vary per
/// deployment (endpoint, bucket, prefix and the two keys) are read from the environment, as
/// ARCHIVE_S3_*.
/// </summary>
public class VolumeBackupOptions
{
    /// <summary>
    /// Hour of the UTC day the nightly run fires. 8 is 03:00 Central in summer, 02:00 in winter.
    /// </summary>
    public int HourUtc { get; set; } = 8;

    /// <summary>
    /// How many archives to keep. Three is three days of history: if data is corrupted and nobody
    /// notices for four days, every copy is already bad. Worth revisiting if this ever holds a
    /// full season.
    /// </summary>
    public int Keep { get; set; } = 3;

    /// <summary>
    /// Refuses to build an archive larger than this. The zip is staged on the container's
    /// ephemeral disk, and Kubernetes evicts a pod that exceeds its ephemeral-storage limit —
    /// a limit UpTurtle has not published. At roughly 10 MB today this is nowhere near
    /// mattering, but it grows with every PDF, so the fuse is here before it does.
    /// </summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;
}
