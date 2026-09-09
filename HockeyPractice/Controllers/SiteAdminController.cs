using HockeyPractice.Infrastructure;
using HockeyPractice.Persistence;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.Util;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace HockeyPractice.Controllers;

/// <summary>
/// Site owner surface: create teams and hand out their first codes.
/// Gated by SITE_ADMIN_CODE, which fails closed — if the variable is unset nothing is accepted,
/// rather than falling back to a default that would be a published backdoor.
/// </summary>
[Route("admin")]
public class SiteAdminController : Controller
{
    /// <summary>Typed back before a restore runs. Uppercase and unlike anything else on the page,
    /// so it cannot be produced by autofill or by mashing Enter.</summary>
    private const string ConfirmWord = "REPLACE";

    /// <summary>
    /// Ceiling on an uploaded backup. Kestrel caps request bodies at 30 MB by default, which a
    /// real database will outgrow, so this raises it deliberately rather than letting the restore
    /// start failing with an unexplained 413 one season from now. It stays well under the 1 GiB
    /// volume; the free-space check is what actually decides whether a given file fits.
    /// </summary>
    private const long MaxRestoreBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Ceiling on an uploaded ARCHIVE, which carries every PDF and diagram as well as the
    /// database, so it outgrows MaxRestoreBytes. The gateway may well cap a request body below
    /// this; that ceiling has never been measured, which is why restoring from the bucket is the
    /// path to prefer and this one is the fallback.
    /// </summary>
    private const long MaxArchiveBytes = 900L * 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly TeamAccessService _access;
    private readonly PlanStorageService _storage;
    private readonly DatabaseBackupService _backup;
    private readonly MaintenanceState _maintenance;
    private readonly BackupRunner _runner;
    private readonly IBackupStore _store;
    private readonly BackupStatus _archiveStatus;
    private readonly StartupHealth _health;
    private readonly VolumeBackupOptions _archiveOptions;
    private readonly DataPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SiteAdminController> _log;
    private readonly string? _adminCodeHash;

    public SiteAdminController(AppDbContext db, TeamAccessService access,
        PlanStorageService storage, DatabaseBackupService backup, MaintenanceState maintenance,
        BackupRunner runner, IBackupStore store, BackupStatus archiveStatus,
        StartupHealth health, IOptions<VolumeBackupOptions> archiveOptions,
        DataPaths paths, IHostApplicationLifetime lifetime, IConfiguration config,
        ILogger<SiteAdminController> log)
    {
        _db = db;
        _access = access;
        _storage = storage;
        _backup = backup;
        _maintenance = maintenance;
        _runner = runner;
        _store = store;
        _archiveStatus = archiveStatus;
        _health = health;
        _archiveOptions = archiveOptions.Value;
        _paths = paths;
        _lifetime = lifetime;
        _log = log;

        var code = config["SITE_ADMIN_CODE"];
        _adminCodeHash = string.IsNullOrWhiteSpace(code) ? null : Security.HashCode(code);
    }

    /// <summary>
    /// TempData key for a notice that must NOT travel in the URL, because it carries a code.
    /// See <see cref="SecretNotice"/>.
    /// </summary>
    private const string SecretNoticeKey = "hp:notice";

    [HttpGet("")]
    public async Task<IActionResult> Index(string? notice)
    {
        if (!_access.IsSiteAdmin(User))
            return View("AdminLogin", new AdminViewModel { Configured = _adminCodeHash is not null });

        // A code-bearing notice arrives out of band; ordinary ones still ride the query string,
        // where they are harmless and survive a reload.
        notice ??= TempData[SecretNoticeKey] as string;

        return View(await BuildAsync(notice));
    }

    /// <summary>
    /// Hands a notice to <see cref="Index"/> without putting it in the URL, and redirects there.
    ///
    /// For anything containing a code. A manager code is the one credential this site stores
    /// hash-only and tells you cannot be read back, and RedirectToAction(new { notice }) wrote it
    /// straight into the query string, which means the browser's history, forever, and the
    /// gateway's access log, where nobody would ever think to look for it. TempData rides an
    /// encrypted cookie instead (the CookieTempDataProvider protects it with the same key ring
    /// that signs the access ticket, and sets its path from PathBase), so the code is shown once
    /// and leaves no trace behind it.
    ///
    /// Not ISession, which this app deliberately does not use: TempData's default provider here
    /// is cookie-backed and needs no server-side state.
    /// </summary>
    private IActionResult SecretNotice(string notice)
    {
        TempData[SecretNoticeKey] = notice;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("code-entry")]
    public async Task<IActionResult> Login(string adminCode)
    {
        if (_adminCodeHash is null || !Security.CodeMatches(adminCode, _adminCodeHash))
        {
            return View("AdminLogin", new AdminViewModel
            {
                Configured = _adminCodeHash is not null,
                Error = _adminCodeHash is null
                    ? "Site admin is not configured. Set SITE_ADMIN_CODE and restart."
                    : "That code didn't work."
            });
        }

        await _access.GrantSiteAdminAsync(HttpContext);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Drops the site-admin claim without touching any team access on this device.</summary>
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _access.RevokeSiteAdminAsync(HttpContext);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost("teams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeam(string name, string? slug, string? timeZoneId)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (string.IsNullOrWhiteSpace(name))
            return View("Index", await BuildAsync(null, "Give the team a name."));

        var candidate = Slugify(string.IsNullOrWhiteSpace(slug) ? name : slug);
        if (candidate.Length == 0)
            return View("Index", await BuildAsync(null, "That name doesn't produce a usable URL. Set the slug manually."));

        if (await _db.Teams.AnyAsync(t => t.Slug == candidate))
            return View("Index", await BuildAsync(null, $"The slug \"{candidate}\" is already taken."));

        // Both codes are shown once, here. Only their hashes are stored.
        var viewCode = Security.NewAccessCode();
        var coachCode = Security.NewAccessCode(8);

        // New teams land at the end of the list rather than defaulting to 0, which would jump
        // them ahead of every existing team.
        var nextSortOrder = await _db.Teams.AnyAsync()
            ? await _db.Teams.MaxAsync(t => t.SortOrder) + 1
            : 0;

        _db.Teams.Add(new Team
        {
            Name = name.Trim(),
            Slug = candidate,
            ViewCode = viewCode,
            ViewCodeHash = Security.HashCode(viewCode),
            CoachCodeHash = Security.HashCode(coachCode),
            TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "America/Chicago" : timeZoneId.Trim(),
            SortOrder = nextSortOrder
        });
        await _db.SaveChangesAsync();

        return SecretNotice(
            $"Created {name.Trim()} — team code {viewCode}, coach code {coachCode}. " +
            "Write these down now; they are not stored and cannot be shown again.");
    }

    /// <summary>
    /// Break-glass: takes manager access to a team without knowing its code.
    ///
    /// Deliberately an action rather than an ambient right. A site admin is not a manager of
    /// every team by default — that silent elevation is what made the roles confusing — but
    /// when they do need in (a coach has left, something is broken), this grants it in one
    /// click and the team header then says so on every page until they hand it back.
    /// </summary>
    [HttpPost("teams/{teamId:int}/enter")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnterTeam(int teamId)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        await _access.GrantTeamAsync(HttpContext, team.Id, TeamAccessLevel.Manager,
            viaSiteAdmin: true);

        _log.LogInformation("Site admin took manager access to team {TeamId} ({Team})",
            team.Id, team.Name);

        return RedirectToAction("Index", "Coach", new { slug = team.Slug });
    }

    /// <summary>
    /// Swaps a team's SortOrder with its neighbour in the current listing, moving it one place
    /// up or down. Swapping (rather than renumbering the whole list) keeps this a single-row
    /// write and is immune to ties or gaps in the existing values.
    /// </summary>
    [HttpPost("teams/{teamId:int}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveTeam(int teamId, string direction)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var ordered = await _db.Teams.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        var index = ordered.FindIndex(t => t.Id == teamId);
        if (index < 0) return NotFound();

        var neighborIndex = direction == "up" ? index - 1 : index + 1;
        if (neighborIndex < 0 || neighborIndex >= ordered.Count)
            return RedirectToAction(nameof(Index));

        (ordered[index].SortOrder, ordered[neighborIndex].SortOrder) =
            (ordered[neighborIndex].SortOrder, ordered[index].SortOrder);

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Issues a fresh pair of codes for a team and shows them once.
    ///
    /// This is the site admin's only route into a team, and it is deliberately indirect: they
    /// can hand out access, but they can't read a team's plans or roster without holding the
    /// code like anyone else. It is also the recovery path when a coach loses theirs.
    /// </summary>
    [HttpPost("teams/{teamId:int}/codes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateCodes(int teamId, string which)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        string notice;
        if (which == "manager")
        {
            var code = Security.NewAccessCode(8);
            team.CoachCodeHash = Security.HashCode(code);
            notice = $"{team.Name} — new manager code {code}. " +
                     "Anyone using the old one will need this instead.";
        }
        else
        {
            var code = Security.NewAccessCode();
            team.ViewCode = code;
            team.ViewCodeHash = Security.HashCode(code);
            notice = $"{team.Name} — new team code {code}. " +
                     "Every player and parent will need to enter this again.";
        }

        await _db.SaveChangesAsync();
        return SecretNotice(notice);
    }

    /// <summary>
    /// Removes a team and everything under it — plans, roster, view history, subscribers, and
    /// the team's directory on disk. Requires the team's name typed back as confirmation,
    /// because this is unrecoverable and a misclick would take a season of plans with it.
    /// </summary>
    [HttpPost("teams/{teamId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(int teamId, string confirmName)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        if (!string.Equals(confirmName?.Trim(), team.Name, StringComparison.OrdinalIgnoreCase))
            return View("Index", await BuildAsync(null,
                $"To delete \"{team.Name}\", type its name exactly to confirm."));

        var name = team.Name;

        // PlanDrills must go first, by hand. A team cascades into BOTH Plans (which cascade on to
        // PlanDrills) and Drills — but PlanDrill -> Drill is Restrict, deliberately, so a drill
        // can't be pulled out from under a published plan. SQLite doesn't define which of a
        // parent's child tables it processes first, so if it reaches Drills while those PlanDrill
        // rows still exist, the restrict fires and the whole delete aborts with a foreign-key
        // error. Verified: without this, deleting a team with a drill in use fails outright.
        var planIds = await _db.Plans.Where(p => p.TeamId == team.Id).Select(p => p.Id).ToListAsync();
        if (planIds.Count > 0)
        {
            _db.PlanDrills.RemoveRange(
                _db.PlanDrills.Where(pd => planIds.Contains(pd.PracticePlanId)));
            await _db.SaveChangesAsync();
        }

        // Cascades handle the remaining child rows; the files on the volume are ours to clean up.
        _db.Teams.Remove(team);
        await _db.SaveChangesAsync();
        _storage.DeleteTeam(teamId);

        return RedirectToAction(nameof(Index), new { notice = $"Deleted {name} and all its plans." });
    }

    // ── Backup and restore ───────────────────────────────────────────────

    /// <summary>
    /// Turns writes off, or back on. The pause is what makes a restore safe, and it lifts itself
    /// after <see cref="MaintenanceState.Window"/> so forgetting about it cannot leave a coach
    /// unable to post Thursday's plan.
    /// </summary>
    [HttpPost("maintenance")]
    [ValidateAntiForgeryToken]
    public IActionResult Maintenance(string action)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (action == "resume")
        {
            _maintenance.Resume();
            _log.LogInformation("Site admin resumed writes");
            return RedirectToAction(nameof(Index), new { notice = "Saving is back on for everyone." });
        }

        _maintenance.Pause();
        _log.LogWarning("Site admin paused writes for {Minutes} minutes",
            (int)MaintenanceState.Window.TotalMinutes);

        return RedirectToAction(nameof(Index), new
        {
            notice = $"Saving is paused for everyone for {(int)MaintenanceState.Window.TotalMinutes} " +
                     "minutes. Reading plans still works. It comes back on by itself, or press " +
                     "Resume saving when you are done."
        });
    }

    /// <summary>
    /// Streams a consistent copy of the database to the browser.
    ///
    /// A POST rather than a link, so it carries an antiforgery token like every other action here
    /// and cannot be set off by a stray URL. It does not require the pause: the snapshot is taken
    /// inside a read transaction, so it is coherent whether or not anyone is writing. Requiring a
    /// pause for a routine backup would only teach people to skip backups.
    /// </summary>
    [HttpPost("backup/download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadBackup(CancellationToken ct)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        // The snapshot lands beside the live database before it is streamed, so a volume with no
        // room left cannot produce one. Better to say so than to fail partway and hand over a
        // truncated file that looks like a backup.
        if (_storage.IsFull())
            return View("Index", await BuildAsync(null,
                "Storage is too full to write a snapshot. Delete some old plans first."));

        string snapshot;
        try
        {
            snapshot = await _backup.SnapshotAsync(null, ct);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not snapshot the database: {Type}: {Error}",
                ex.GetType().FullName, ex.Message);
            return View("Index", await BuildAsync(null,
                "Could not take a copy of the database. The log has the detail."));
        }

        _log.LogInformation("Site admin downloaded a database backup ({Bytes} bytes)",
            new FileInfo(snapshot).Length);

        // DeleteOnClose: the framework streams the file and disposes the handle, and the snapshot
        // goes with it. Without this a full copy of the database piles up on the volume on every
        // download, and the volume is the same 1 GiB everything else lives on.
        var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose);

        return File(stream, "application/vnd.sqlite3",
            $"hockeypractice-{DateTime.UtcNow:yyyy-MM-dd-HHmm}.db");
    }

    /// <summary>
    /// Replaces the live database with an uploaded one, then restarts so the app reopens it
    /// cleanly and applies any schema changes the backup predates.
    ///
    /// The most destructive action on the site, so it is fenced three ways: writes must already
    /// be paused, the word has to be typed, and the file has to prove it is a database this build
    /// can actually run against. The database it replaces is kept, which is the only way back
    /// from restoring the wrong file.
    /// </summary>
    [HttpPost("backup/restore")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxRestoreBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRestoreBytes)]
    public async Task<IActionResult> RestoreBackup(IFormFile? backup, string? confirm,
        CancellationToken ct)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (!_maintenance.IsPaused)
            return View("Index", await BuildAsync(null,
                "Pause saving first. Replacing the database while people are still writing to it " +
                "would throw away whatever they were in the middle of."));

        if (!string.Equals(confirm?.Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase))
            return View("Index", await BuildAsync(null,
                $"Type {ConfirmWord} to confirm. This replaces every team, plan and drill on the " +
                "site with whatever is in the file."));

        if (backup is null || backup.Length == 0)
            return View("Index", await BuildAsync(null, "Choose a backup file to upload."));

        // The uploaded file has to fit beside the one it replaces, because the old one is kept.
        var headroom = Math.Max(0, _storage.QuotaBytes - _storage.UsedBytes());
        if (backup.Length > headroom)
            return View("Index", await BuildAsync(null,
                $"That file is {PlanStorageService.Human(backup.Length)} and there is only " +
                $"{PlanStorageService.Human(headroom)} free. Delete some old plans first."));

        // Staged on the volume rather than validated in memory: it has to land on the same
        // filesystem to be moved into place atomically, and it may be larger than is sensible to
        // hold in RAM.
        var staged = Path.Combine(_paths.Root, $"restore-{Guid.NewGuid():N}.db");
        try
        {
            await using (var destination = System.IO.File.Create(staged))
            await using (var source = backup.OpenReadStream())
            {
                await source.CopyToAsync(destination, ct);
            }

            // GetMigrations is every schema change this build knows about. A backup carrying one
            // it does not is from a newer version and gets refused rather than half-loaded.
            var check = await _backup.ValidateAsync(staged, _db.Database.GetMigrations(), ct);
            if (!check.Ok)
                return View("Index", await BuildAsync(null, check.Error));

            // Re-checked here, not just at the top. Everything above — a slow upload on rink
            // wifi, integrity_check over a large file — can outlast the 30-minute pause, and
            // writes landing between the check and the swap would go into the file that is about
            // to become the undo copy.
            if (!_maintenance.IsPaused)
                return View("Index", await BuildAsync(null,
                    "Saving switched back on while the file was uploading, so nothing was " +
                    "changed. Pause saving and try again."));

            _backup.Swap(staged);
            staged = null;
        }
        catch (SqliteException ex)
        {
            // The checkpoint refusing to run. It happens before anything moves, so the database
            // really is untouched.
            _log.LogError("Restore aborted before the swap: {Error}", ex.Message);
            return View("Index", await BuildAsync(null,
                "The database could not be quiesced, so it was left alone and nothing was changed."));
        }
        catch (IOException ex)
        {
            _log.LogError("Restore failed while moving files: {Error}", ex.Message);
            return View("Index", await BuildAsync(null,
                "Could not write the file to storage, so nothing was changed."));
        }
        finally
        {
            // A finally, not a catch. The old code cleaned up only on IOException, so a cancelled
            // upload (OperationCanceledException) or any other failure stranded a full-size
            // database on a volume with no resize path and nothing to ever remove it. Nulled
            // above on success, where Swap has already consumed the file. The sidecars go too:
            // validating a WAL-marked upload leaves its own -wal and -shm beside the staged file.
            if (staged is not null)
            {
                TryDelete(staged);
                DatabaseBackupService.DeleteSidecars(staged);
            }
        }

        // Restart rather than carry on against a swapped file. A fresh process reopens the
        // database, runs any migrations the backup predates, and leaves nothing anywhere still
        // holding the old one. Delayed so this response reaches the browser first.
        ScheduleRestart();

        return View("Restored");
    }

    // ── Off-site backups ─────────────────────────────────────────────────

    /// <summary>
    /// Builds an archive and uploads it now, rather than waiting for tonight.
    ///
    /// No pause required, same reasoning as the database download: the database half is a
    /// snapshot taken inside a read transaction, and requiring a pause for a routine backup only
    /// teaches people to skip backups. Unlike the nightly run this does not skip while writes are
    /// paused, because someone pressing the button has decided.
    /// </summary>
    [HttpPost("backup/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveNow(CancellationToken ct)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var result = await _runner.RunAsync("manual", skipWhilePaused: false, ct);

        if (result.AlreadyRunning)
            return RedirectToAction(nameof(Index), new
            {
                notice = "An archive is already running. Give it a minute and reload."
            });

        if (!result.Ok)
            return View("Index", await BuildAsync(null,
                $"The archive failed. {result.Error}"));

        var pruned = result.Pruned == 0
            ? ""
            : $" {result.Pruned} older archive{(result.Pruned == 1 ? " was" : "s were")} removed.";

        return RedirectToAction(nameof(Index), new
        {
            notice = $"Archived {result.Uploaded!.Name} " +
                     $"({PlanStorageService.Human(result.Uploaded.Bytes)}) off site.{pruned}"
        });
    }

    /// <summary>
    /// Streams a full archive to the browser, without putting it in the bucket.
    ///
    /// This is the answer to the Cloudflare account itself going away, since R2 and the DNS live
    /// behind the same login. Worth pressing occasionally even when the nightly job is healthy.
    /// </summary>
    [HttpPost("backup/archive/download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadArchive(CancellationToken ct)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        BackupResult? built;
        try
        {
            built = await _runner.BuildForDownloadAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not build an archive for download: {Type}: {Error}",
                ex.GetType().FullName, ex.Message);
            return View("Index", await BuildAsync(null,
                "Could not build the archive. The log has the detail."));
        }

        if (built is null)
            return View("Index", await BuildAsync(null,
                "An archive is already running. Give it a minute and try again."));

        _log.LogInformation("Site admin downloaded a full archive ({Bytes} bytes)", built.Bytes);

        // DeleteOnClose, as the database download does: the framework streams the file and
        // disposes the handle, and the archive goes with it. Without this a full copy of the
        // volume piles up on the container's disk on every download.
        var stream = new FileStream(built.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose);

        return File(stream, "application/zip", Path.GetFileName(built.Path));
    }

    /// <summary>
    /// Replaces the whole site — database and files — from an archive, then restarts.
    ///
    /// Takes either an archive already in the bucket, by key, or an uploaded zip. Picking from
    /// the bucket is the path to prefer: it downloads server side, so the archive never travels
    /// in a request body and cannot meet whatever size ceiling the gateway imposes. Plan PDFs cap
    /// at 15 MB, so nothing has ever tested a larger body through it.
    /// </summary>
    [HttpPost("backup/archive/restore")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxArchiveBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxArchiveBytes)]
    public async Task<IActionResult> RestoreArchive(IFormFile? archive, string? key, string? confirm,
        CancellationToken ct)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (!_maintenance.IsPaused)
            return View("Index", await BuildAsync(null,
                "Pause saving first. Replacing everything while people are still writing would " +
                "throw away whatever they were in the middle of."));

        if (!string.Equals(confirm?.Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase))
            return View("Index", await BuildAsync(null,
                $"Type {ConfirmWord} to confirm. This replaces every team, plan, drill and " +
                "uploaded file on the site with whatever is in the backup."));

        // Staged on the container's ephemeral disk, not the volume. Only the database inside it
        // has to land on the volume, and validation puts it there.
        var staging = Path.Combine(Path.GetTempPath(), "hockeypractice-backup");
        Directory.CreateDirectory(staging);
        var local = Path.Combine(staging, $"incoming-{Guid.NewGuid():N}.zip");
        var fetched = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                // Matched against the listing rather than trusted. The store only ever lists keys
                // under its own prefix that match the archive name pattern, so going through it
                // means a hand-edited form cannot name an arbitrary object in the bucket.
                var available = await _store.ListAsync(ct);
                if (available.FirstOrDefault(a => a.Key == key) is null)
                    return View("Index", await BuildAsync(null,
                        "That backup is no longer in the bucket. Reload and pick another."));

                var downloaded = await _runner.FetchAsync(key, ct);
                System.IO.File.Move(downloaded, local, overwrite: true);
                fetched = true;
            }
            else if (archive is not null && archive.Length > 0)
            {
                await using var destination = System.IO.File.Create(local);
                await using var source = archive.OpenReadStream();
                await source.CopyToAsync(destination, ct);
                fetched = true;
            }

            if (!fetched)
                return View("Index", await BuildAsync(null,
                    "Choose a backup to restore, either from the list or by uploading a zip."));

            var result = await _runner.RestoreAsync(local, ct);
            if (!result.Ok)
                return View("Index", await BuildAsync(null, result.Error));
        }
        finally
        {
            TryDelete(local);
        }

        ScheduleRestart();

        ViewBag.IncludedFiles = true;
        return View("Restored");
    }

    /// <summary>
    /// Streams the database a restore moved aside.
    ///
    /// Without this the undo copy is written, displayed, and reachable by nobody: there is no
    /// shell into the container, so "the way back from restoring the wrong file" was a claim the
    /// site could not honour. Downloading it puts it back in the admin's hands, where the
    /// existing restore can take it.
    ///
    /// No DeleteOnClose here, unlike the snapshot downloads — this is the only copy of that
    /// database and streaming it must not consume it.
    /// </summary>
    [HttpPost("backup/replaced/download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadReplaced()
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (!System.IO.File.Exists(_backup.ReplacedDatabase))
            return View("Index", await BuildAsync(null, "There is no replaced database to download."));

        _log.LogInformation("Site admin downloaded the replaced database");

        var stream = new FileStream(_backup.ReplacedDatabase, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        return File(stream, "application/vnd.sqlite3",
            $"hockeypractice-replaced-{_backup.ReplacedAtUtc:yyyy-MM-dd-HHmm}.db");
    }

    /// <summary>
    /// Removes the kept undo copy, which is one database in size and otherwise sits on the volume
    /// for good. Worth doing once a restore has been confirmed good, on a volume capped at 1 GiB
    /// with no resize path.
    /// </summary>
    [HttpPost("backup/replaced/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReplaced()
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var bytes = _backup.ReplacedBytes;
        TryDelete(_backup.ReplacedDatabase);
        DatabaseBackupService.DeleteSidecars(_backup.ReplacedDatabase);

        _log.LogWarning("Site admin deleted the replaced database ({Bytes} bytes)", bytes);

        return RedirectToAction(nameof(Index), new
        {
            notice = $"Deleted the kept copy and freed {PlanStorageService.Human(bytes)}. " +
                     "There is no longer a way back from the last restore."
        });
    }

    /// <summary>
    /// Steps down after the response has gone out. A fresh process reopens the swapped file and
    /// runs any migrations the backup predates.
    /// </summary>
    private void ScheduleRestart() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        _lifetime.StopApplication();
    });

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { /* best effort; the staged copy is disposable */ }
    }

    private async Task<AdminViewModel> BuildAsync(string? notice, string? error = null)
    {
        var teams = await _db.Teams
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new TeamSummary
            {
                Team = t,
                PlanCount = t.Plans.Count,
                PlayerCount = t.Players.Count
            })
            .ToListAsync();

        // Listing R2 on every admin page load is a network call, so it is wrapped: if the
        // bucket is unreachable the page still renders and SAYS so. Swallowing this into an
        // empty list would read as "you have no backups", which is the most alarming possible
        // way to report a transient network error.
        IReadOnlyList<StoredBackup> archives = Array.Empty<StoredBackup>();
        string? archiveListError = null;
        if (_store.Enabled)
        {
            try
            {
                archives = await _store.ListAsync();
            }
            catch (Exception ex)
            {
                archiveListError = $"{ex.GetType().Name}: {ex.Message}";
                _log.LogError("Could not list the archive bucket: {Error}", archiveListError);
            }
        }

        return new AdminViewModel
        {
            Teams = teams,
            Notice = notice,
            Error = error,
            Configured = _adminCodeHash is not null,
            UsedBytes = _storage.UsedBytes(),
            QuotaBytes = _storage.QuotaBytes,
            WritesPaused = _maintenance.IsPaused,
            PauseMinutesLeft = _maintenance.MinutesLeft,
            DatabaseBytes = _backup.DatabaseBytes,
            ReplacedBytes = _backup.ReplacedBytes,
            ReplacedAtUtc = _backup.ReplacedAtUtc,
            ReplacedPath = _backup.ReplacedDatabase,
            ArchivingConfigured = _store.Enabled,
            Archives = archives,
            ArchiveListError = archiveListError,
            ArchiveLastAttemptUtc = _archiveStatus.LastAttemptUtc,
            ArchiveLastAttemptFailed = _archiveStatus.LastAttemptFailed,
            ArchiveError = _archiveStatus.LastError,
            ArchiveRunning = _archiveStatus.Running,
            ArchiveKeep = _archiveOptions.Keep,
            ArchiveHourUtc = _archiveOptions.HourUtc,
            MigrationError = _health.MigrationError
        };
    }

    private static string Slugify(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
