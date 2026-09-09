using HockeyPractice.Infrastructure;
using Microsoft.Extensions.Options;

namespace HockeyPractice.Services;

/// <summary>
/// Runs the volume archive once a day, and once at startup if the last one is stale.
///
/// The startup catch-up is what makes "scheduled" mean anything here. There is one replica and it
/// restarts on every deploy, so without it a pod that happens to restart across the nightly window
/// silently skips a day, and nobody finds out until the day they need the archive.
/// </summary>
public class ScheduledBackupService : BackgroundService
{
    /// <summary>
    /// How stale the newest archive has to be before a starting pod backs up immediately. Longer
    /// than a day would never fire; much shorter would archive on every deploy.
    /// </summary>
    private static readonly TimeSpan CatchUpAfter = TimeSpan.FromHours(20);

    /// <summary>
    /// Grace before the catch-up, so a deploy is not competing with the traffic that arrives as
    /// the pod goes into the load balancer.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A restore this recent means the catch-up stays out of the way. It restarts the app, so
    /// two minutes later this service would otherwise back up the just-restored volume and spend
    /// a retention slot on it while someone is still checking whether the restore was right.
    /// </summary>
    private static readonly TimeSpan AfterRestoreQuiet = TimeSpan.FromHours(1);

    private readonly BackupRunner _runner;
    private readonly IBackupStore _store;
    private readonly DataPaths _paths;
    private readonly VolumeBackupOptions _options;
    private readonly ILogger<ScheduledBackupService> _log;

    public ScheduledBackupService(BackupRunner runner, IBackupStore store, DataPaths paths,
        IOptions<VolumeBackupOptions> options, ILogger<ScheduledBackupService> log)
    {
        _runner = runner;
        _store = store;
        _paths = paths;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // An await BEFORE anything that could throw, and this is not a style preference.
        // ExecuteAsync runs inline until its first await, so a synchronous throw here propagates
        // out of StartAsync and stops the host: a crash loop at startup rather than a failed
        // backup. Everything after this point is on a background thread and is caught below.
        await Task.Yield();

        if (!_store.Enabled)
        {
            _log.LogInformation("Off-site backups are not configured, so nothing is scheduled.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            await CatchUpAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // The catch-up lists the bucket, which can fail exactly as readily as an upload can.
            // Outside this try it would be an unhandled background exception and the site would
            // go down because a backup could not check whether it was due.
            _log.LogError("The startup archive check failed. {Type}: {Error}",
                ex.GetType().Name, ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(UntilNextRun(DateTime.UtcNow), stoppingToken);
                await _runner.RunAsync("nightly", skipWhilePaused: true, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // RunAsync already swallows its own failures, so reaching here means something
                // outside it went wrong. Caught anyway: since .NET 6 the default
                // BackgroundServiceExceptionBehavior is StopHost, and no backup is worth the site.
                _log.LogError("The nightly archive loop failed. {Type}: {Error}",
                    ex.GetType().Name, ex.Message);
            }
        }
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        if (_paths.LastRestoreUtc is { } restored &&
            DateTime.UtcNow - restored < AfterRestoreQuiet)
        {
            _log.LogInformation("Skipping the startup archive: a restore finished at {Restored}. " +
                                "The next scheduled run picks it up.", restored);
            return;
        }

        var existing = await _store.ListAsync(ct);
        var newest = existing.FirstOrDefault();

        if (newest is not null && DateTime.UtcNow - newest.CreatedUtc < CatchUpAfter)
        {
            _log.LogInformation("Newest archive {Name} is recent enough, nothing to catch up.",
                newest.Name);
            return;
        }

        _log.LogInformation("Newest archive is {State}, running one now.",
            newest is null ? "missing" : $"from {newest.CreatedUtc:u}");

        await _runner.RunAsync("startup catch-up", skipWhilePaused: true, ct);
    }

    /// <summary>How long until the next <see cref="VolumeBackupOptions.HourUtc"/>.</summary>
    internal static TimeSpan UntilNextRun(DateTime nowUtc, int hourUtc)
    {
        hourUtc = Math.Clamp(hourUtc, 0, 23);
        var next = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hourUtc, 0, 0, DateTimeKind.Utc);
        if (next <= nowUtc) next = next.AddDays(1);
        return next - nowUtc;
    }

    private TimeSpan UntilNextRun(DateTime nowUtc) => UntilNextRun(nowUtc, _options.HourUtc);
}
