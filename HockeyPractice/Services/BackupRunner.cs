using HockeyPractice.Infrastructure;
using HockeyPractice.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPractice.Services;

/// <summary>
/// What happened the last time an archive was attempted, for the admin page.
///
/// Deliberately only the CURRENT process's story. The last successful run's time and size are
/// read from the bucket instead, because this is lost on every restart and a restore restarts on
/// purpose — a page that said "never run" while three good archives sat in R2 would be telling
/// the exact opposite of the truth to someone checking whether their backups work.
/// </summary>
public class BackupStatus
{
    private readonly object _gate = new();

    public DateTime? LastAttemptUtc { get; private set; }
    public bool LastAttemptFailed { get; private set; }
    public string? LastError { get; private set; }
    public bool Running { get; private set; }

    public void Started()
    {
        lock (_gate) Running = true;
    }

    public void Succeeded()
    {
        lock (_gate)
        {
            Running = false;
            LastAttemptUtc = DateTime.UtcNow;
            LastAttemptFailed = false;
            LastError = null;
        }
    }

    public void Failed(string error)
    {
        lock (_gate)
        {
            Running = false;
            LastAttemptUtc = DateTime.UtcNow;
            LastAttemptFailed = true;
            LastError = error;
        }
    }

    public void Idle()
    {
        lock (_gate) Running = false;
    }
}

public record BackupRunResult(
    bool Ok,
    string? Error = null,
    StoredBackup? Uploaded = null,
    int Pruned = 0,
    bool AlreadyRunning = false,
    bool SkippedWhilePaused = false);

/// <summary>
/// Builds one archive, uploads it, and prunes old ones. Shared by the nightly schedule and the
/// Archive now button so there is exactly one description of what a backup run is.
///
/// The run order is the safety property: build, upload, <em>confirm</em>, then prune. A failed
/// upload must never be able to reduce the number of copies you hold.
/// </summary>
public class BackupRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly VolumeBackupService _volume;
    private readonly IBackupStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly MaintenanceState _maintenance;
    private readonly BackupStatus _status;
    private readonly VolumeBackupOptions _options;
    private readonly ILogger<BackupRunner> _log;

    public BackupRunner(VolumeBackupService volume, IBackupStore store, IServiceScopeFactory scopes,
        MaintenanceState maintenance, BackupStatus status, IOptions<VolumeBackupOptions> options,
        ILogger<BackupRunner> log)
    {
        _volume = volume;
        _store = store;
        _scopes = scopes;
        _maintenance = maintenance;
        _status = status;
        _options = options.Value;
        _log = log;
    }

    /// <summary>
    /// Runs one archive. Never throws: every failure comes back in the result and is recorded, so
    /// a caller on a background thread cannot take the host down with it.
    /// </summary>
    /// <param name="skipWhilePaused">
    /// True for the nightly run. Writes are paused during a restore, and an archive started then
    /// would capture a half-restored volume and spend a retention slot on it. The button does not
    /// skip, because someone pressing it has decided.
    /// </param>
    public async Task<BackupRunResult> RunAsync(string reason, bool skipWhilePaused,
        CancellationToken ct = default)
    {
        if (!_store.Enabled)
            return new BackupRunResult(false, "Off-site backups are not configured.");

        // Zero timeout, so a second caller is told rather than queued behind a run that may take
        // minutes. The nightly job and the button can otherwise collide.
        if (!await _gate.WaitAsync(0, ct))
            return new BackupRunResult(false, "An archive is already running.", AlreadyRunning: true);

        try
        {
            if (skipWhilePaused && _maintenance.IsPaused)
            {
                _log.LogInformation("Skipped the {Reason} archive: writes are paused", reason);
                return new BackupRunResult(true, SkippedWhilePaused: true);
            }

            _status.Started();

            // The container's ephemeral disk, never the volume. A backup must not need free space
            // on the disk it exists to protect, and on a full volume is exactly when you want one.
            var staging = Path.Combine(Path.GetTempPath(), "hockeypractice-backup");
            Directory.CreateDirectory(staging);

            var name = VolumeBackupService.NameFor(DateTime.UtcNow);
            var local = Path.Combine(staging, name);

            try
            {
                List<string> migrations;
                using (var scope = _scopes.CreateScope())
                {
                    // A singleton cannot hold a scoped DbContext, and the manifest needs the
                    // schema history so a restore can refuse an archive from a newer build.
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    migrations = db.Database.GetMigrations().ToList();
                }

                var built = await _volume.CreateAsync(local, migrations, ct);

                var key = _store.KeyFor(name);
                await _store.UploadAsync(local, key, ct);

                // Confirm before pruning. A PUT that returns without the object landing would
                // otherwise let retention delete a good archive to make room for one that is not
                // there.
                var stored = await _store.SizeAsync(key, ct);
                if (stored is null)
                {
                    var message = "The upload reported success but the archive is not in the bucket.";
                    _log.LogError("{Message} Key {Key}", message, key);
                    _status.Failed(message);
                    return new BackupRunResult(false, message);
                }

                var uploaded = new StoredBackup(key, name, stored.Value, DateTime.UtcNow);
                var pruned = await PruneAsync(ct);

                _status.Succeeded();
                _log.LogInformation("{Reason} archive complete: {Name}, {Bytes} bytes, {Files} " +
                                    "files, {Pruned} pruned", reason, name, built.Bytes,
                                    built.FileCount, pruned);

                return new BackupRunResult(true, Uploaded: uploaded, Pruned: pruned);
            }
            finally
            {
                TryDelete(local);
            }
        }
        catch (Exception ex)
        {
            // Catches everything on purpose. This runs from a BackgroundService, and since .NET 6
            // an unhandled exception there stops the host: a transient R2 outage would take the
            // whole site down because a backup failed.
            var message = $"{ex.GetType().Name}: {ex.Message}";
            _log.LogError("The {Reason} archive failed. {Error}", reason, message);
            _status.Failed(message);
            return new BackupRunResult(false, message);
        }
        finally
        {
            _status.Idle();
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds an archive for the browser to download, without uploading or pruning anything. The
    /// caller owns the file and must delete it; the controller hands it to a FileStream opened
    /// with DeleteOnClose so it goes when the response is done.
    ///
    /// Behind the same gate as a scheduled run, because both write a full copy of the volume to
    /// the container's ephemeral disk and two at once is how a pod gets evicted. Returns null
    /// when a run is already in progress.
    /// </summary>
    public async Task<BackupResult?> BuildForDownloadAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return null;

        try
        {
            var staging = Path.Combine(Path.GetTempPath(), "hockeypractice-backup");
            Directory.CreateDirectory(staging);

            List<string> migrations;
            using (var scope = _scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                migrations = db.Database.GetMigrations().ToList();
            }

            var name = VolumeBackupService.NameFor(DateTime.UtcNow);
            return await _volume.CreateAsync(Path.Combine(staging, name), migrations, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes the oldest archives down to <see cref="VolumeBackupOptions.Keep"/>.
    ///
    /// Two floors. It does nothing at all when Keep is below 1, so a stray 0 cannot delete every
    /// backup there is; and it only ever sees keys the store has already filtered to this app's
    /// prefix and name pattern, so nothing else in the bucket is reachable from here.
    /// </summary>
    private async Task<int> PruneAsync(CancellationToken ct)
    {
        if (_options.Keep < 1)
        {
            _log.LogWarning("Retention is set to {Keep}, so nothing was pruned", _options.Keep);
            return 0;
        }

        var all = await _store.ListAsync(ct);
        var stale = all.Skip(_options.Keep).ToList();

        foreach (var old in stale)
            await _store.DeleteAsync(old.Key, ct);

        return stale.Count;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not clean up the staged archive {Path}: {Error}", path, ex.Message);
        }
    }
}
