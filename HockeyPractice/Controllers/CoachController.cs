using HockeyPractice.Persistence;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.Util;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

[Route("t/{slug}/manage")]
public class CoachController : TeamScopedController
{
    private readonly PlanStorageService _storage;
    private readonly LinkExtractionService _links;
    private readonly DataPaths _paths;
    private readonly NotificationService _notifications;
    private readonly VideoTitleService _videoTitles;
    private readonly ILogger<CoachController> _log;

    private static readonly string[] AllowedLogoTypes =
        ["image/jpeg", "image/png", "image/gif", "image/webp"];
    private const long MaxLogoBytes = 2 * 1024 * 1024;

    public CoachController(AppDbContext db, TeamAccessService access, PlanStorageService storage,
        LinkExtractionService links, DataPaths paths, NotificationService notifications,
        VideoTitleService videoTitles, ILogger<CoachController> log)
        : base(db, access)
    {
        _storage = storage;
        _links = links;
        _paths = paths;
        _notifications = notifications;
        _videoTitles = videoTitles;
        _log = log;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, string? notice)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plans = await Db.Plans
            .Where(p => p.TeamId == ctx!.Team.Id)
            .Select(p => new { Plan = p, Videos = p.Links.Count(l => !l.IsHidden) })
            .OrderByDescending(x => x.Plan.PracticeDateLocal)
            .ToListAsync();

        ViewBag.NavSection = "manage";
        return View(new ManageViewModel
        {
            Ctx = ctx!,
            Plans = plans.Select(x => new PlanCard
            {
                Plan = x.Plan,
                VideoCount = x.Videos,
                WhenLabel = WhenLabel.For(x.Plan.PracticeDateLocal, ctx!.Team.TimeZoneId)
            }).ToList(),
            Roster = await Db.Players.Where(p => p.TeamId == ctx!.Team.Id)
                        .OrderBy(p => p.Name).ToListAsync(),
            ConfirmedSubscribers = await Db.Subscribers
                        .CountAsync(s => s.TeamId == ctx!.Team.Id && s.ConfirmedUtc != null),
            UsedBytes = _storage.UsedBytes(),
            QuotaBytes = _storage.QuotaBytes,
            Notice = notice
        });
    }

    // ── Plans ────────────────────────────────────────────────────────────

    [HttpGet("plans/new")]
    public async Task<IActionResult> NewPlan(string slug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        ViewBag.NavSection = "manage";
        return View("EditPlan", new PlanEditViewModel
        {
            Ctx = ctx!,
            DefaultDate = NextPracticeSlot(ctx!.Team.TimeZoneId),
            MaxUploadBytes = _storage.QuotaBytes,
            Error = _storage.IsFull()
                ? "Storage is nearly full. Delete some old plans before uploading a new one."
                : null
        });
    }

    [HttpPost("plans/new")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> NewPlan(string slug, string title, DateTime practiceDate,
        string? location, string? coachNotes, IFormFile? file)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var error = _storage.ValidateUpload(file);
        if (string.IsNullOrWhiteSpace(title)) error ??= "Give the plan a title.";

        if (error is not null)
        {
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx!, Error = error,
                // A default(DateTime) from failed binding renders as year 1 — fall back.
                DefaultDate = practiceDate == default ? NextPracticeSlot(ctx!.Team.TimeZoneId) : practiceDate,
                MaxUploadBytes = _storage.QuotaBytes,
                RetainedTitle = title, RetainedLocation = location, RetainedNotes = coachNotes
            });
        }

        var plan = new PracticePlan
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            PracticeDateLocal = practiceDate,
            Location = location?.Trim(),
            CoachNotes = coachNotes?.Trim(),
            OriginalFileName = SafeFileName(file!.FileName),
            Status = PlanStatus.Draft
        };

        // Saved first so the plan has an id to key its directory off.
        Db.Plans.Add(plan);
        await Db.SaveChangesAsync();

        var saved = await _storage.SaveAsync(ctx.Team.Id, plan.Id, file);
        if (!saved.Ok)
        {
            Db.Plans.Remove(plan);
            await Db.SaveChangesAsync();
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx, DefaultDate = practiceDate, Error = saved.Error,
                MaxUploadBytes = _storage.QuotaBytes,
                RetainedTitle = title, RetainedLocation = location, RetainedNotes = coachNotes
            });
        }

        plan.ByteSize = saved.Bytes;

        // Extraction is a convenience — if it finds nothing the plan still uploads and renders.
        var extracted = _links.Extract(_paths.PlanPdf(ctx.Team.Id, plan.Id));

        // Best-effort: fills in names for bare URLs the document didn't describe. Never fatal —
        // if egress is blocked or slow, the PDF-derived names stand.
        await _videoTitles.PopulateTitlesAsync(extracted);
        LinkExtractionService.ApplyVideoTitles(extracted);

        foreach (var link in extracted)
        {
            link.PracticePlanId = plan.Id;
            Db.PlanLinks.Add(link);
        }

        await Db.SaveChangesAsync();
        _log.LogInformation("Plan {PlanId} uploaded for team {TeamId} ({Bytes} bytes)",
            plan.Id, ctx.Team.Id, saved.Bytes);

        return RedirectToAction(nameof(EditPlan), new { slug, id = plan.Id });
    }

    [HttpGet("plans/{id:int}")]
    public async Task<IActionResult> EditPlan(string slug, int id, string? notice)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        ViewBag.NavSection = "manage";
        return View(new PlanEditViewModel
        {
            Ctx = ctx!,
            Plan = plan,
            Links = plan.Links.OrderBy(l => l.SortOrder).ToList(),
            DefaultDate = plan.PracticeDateLocal,
            MaxUploadBytes = _storage.QuotaBytes,
            Notice = notice
        });
    }

    [HttpPost("plans/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPlan(string slug, int id, string title,
        DateTime practiceDate, string? location, string? coachNotes,
        int[]? linkId, string[]? linkLabel, int[]? visibleLinkId)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        plan.Title = string.IsNullOrWhiteSpace(title) ? plan.Title : title.Trim();
        plan.PracticeDateLocal = practiceDate;
        plan.Location = location?.Trim();
        plan.CoachNotes = coachNotes?.Trim();

        // Labels arrive parallel to ids. The checkboxes are "show to players", so the ones
        // that arrive are the visible links — an unticked box submits nothing at all.
        var visible = (visibleLinkId ?? []).ToHashSet();
        if (linkId is not null)
        {
            for (var i = 0; i < linkId.Length; i++)
            {
                var link = plan.Links.FirstOrDefault(l => l.Id == linkId[i]);
                if (link is null) continue;

                if (linkLabel is not null && i < linkLabel.Length && !string.IsNullOrWhiteSpace(linkLabel[i]))
                {
                    var typed = linkLabel[i].Trim();
                    // Only count it as a coach edit if they actually changed something.
                    if (!string.Equals(typed, link.Label, StringComparison.Ordinal))
                        link.WasEditedByCoach = true;
                    link.Label = typed;
                }

                var nowHidden = !visible.Contains(link.Id);
                if (link.IsHidden != nowHidden)
                    link.WasEditedByCoach = true;
                link.IsHidden = nowHidden;
                link.SortOrder = i;
            }
        }

        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(EditPlan), new { slug, id });
    }

    /// <summary>
    /// Re-runs extraction over the stored PDF. Names are worked out at upload time, so a plan
    /// uploaded before an improvement keeps its old labels until this is run. Any label the
    /// coach edited by hand is preserved — re-extracting must not undo their corrections.
    /// </summary>
    [HttpPost("plans/{id:int}/reextract")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReExtract(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        if (!_storage.Exists(ctx!.Team.Id, plan.Id))
            return RedirectToAction(nameof(EditPlan), new { slug, id });

        // Remember the coach's own wording and visibility choices, keyed by URL.
        var edited = plan.Links
            .Where(l => l.WasEditedByCoach)
            .ToDictionary(l => l.Url, l => (l.Label, l.IsHidden), StringComparer.OrdinalIgnoreCase);

        var fresh = _links.Extract(_paths.PlanPdf(ctx.Team.Id, plan.Id));
        await _videoTitles.PopulateTitlesAsync(fresh);
        LinkExtractionService.ApplyVideoTitles(fresh);

        foreach (var link in fresh)
        {
            if (!edited.TryGetValue(link.Url, out var keep)) continue;
            link.Label = keep.Label;
            link.IsHidden = keep.IsHidden;
            link.WasEditedByCoach = true;
        }

        Db.PlanLinks.RemoveRange(plan.Links);
        foreach (var link in fresh)
        {
            link.PracticePlanId = plan.Id;
            Db.PlanLinks.Add(link);
        }

        await Db.SaveChangesAsync();
        _log.LogInformation("Re-extracted {Count} links for plan {PlanId}", fresh.Count, plan.Id);

        return RedirectToAction(nameof(EditPlan), new { slug, id });
    }

    /// <summary>
    /// Swaps the PDF on an existing plan without disturbing anything else — the shareable URL,
    /// the view history, and publish state all stay put.
    ///
    /// Deleting and re-uploading was the only way to fix a typo, and that generated a new plan
    /// id, so any link already pasted into the team chat 404'd, the coach's "12 of 17 viewed"
    /// reset to zero even though most of the team had already read it, and any relabelled or
    /// hidden video links were lost. This keeps the id and reuses the same preserve-edits pass
    /// ReExtract uses, so a coach's own wording survives a replacement too.
    /// </summary>
    [HttpPost("plans/{id:int}/replace")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> ReplaceFile(string slug, int id, IFormFile? file)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        var error = _storage.ValidateUpload(file);
        if (error is not null)
        {
            ViewBag.NavSection = "manage";
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx!, Plan = plan, Links = plan.Links.OrderBy(l => l.SortOrder).ToList(),
                DefaultDate = plan.PracticeDateLocal, MaxUploadBytes = _storage.QuotaBytes,
                Error = error
            });
        }

        // Remember the coach's own wording and visibility choices, keyed by URL — the same
        // preservation ReExtract uses, so replacing the file doesn't undo a correction someone
        // already made.
        var edited = plan.Links
            .Where(l => l.WasEditedByCoach)
            .ToDictionary(l => l.Url, l => (l.Label, l.IsHidden), StringComparer.OrdinalIgnoreCase);

        var saved = await _storage.SaveAsync(ctx!.Team.Id, plan.Id, file!);
        if (!saved.Ok)
        {
            ViewBag.NavSection = "manage";
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx, Plan = plan, Links = plan.Links.OrderBy(l => l.SortOrder).ToList(),
                DefaultDate = plan.PracticeDateLocal, MaxUploadBytes = _storage.QuotaBytes,
                Error = saved.Error
            });
        }

        plan.OriginalFileName = SafeFileName(file!.FileName);
        plan.ByteSize = saved.Bytes;

        var fresh = _links.Extract(_paths.PlanPdf(ctx.Team.Id, plan.Id));
        await _videoTitles.PopulateTitlesAsync(fresh);
        LinkExtractionService.ApplyVideoTitles(fresh);

        foreach (var link in fresh)
        {
            if (!edited.TryGetValue(link.Url, out var keep)) continue;
            link.Label = keep.Label;
            link.IsHidden = keep.IsHidden;
            link.WasEditedByCoach = true;
        }

        Db.PlanLinks.RemoveRange(plan.Links);
        foreach (var link in fresh)
        {
            link.PracticePlanId = plan.Id;
            Db.PlanLinks.Add(link);
        }

        await Db.SaveChangesAsync();
        _log.LogInformation("Replaced PDF for plan {PlanId} on team {TeamId} ({Bytes} bytes)",
            plan.Id, ctx.Team.Id, saved.Bytes);

        return RedirectToAction(nameof(EditPlan),
            new { slug, id, notice = "Replaced the PDF. Links were re-read from the new file." });
    }

    [HttpPost("plans/{id:int}/publish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        // Genuinely the first publish, not a republish after an unpublish. Keying off
        // PublishedUtc rather than Status is what stops a fix-and-republish from mailing
        // the whole team a second time.
        var firstPublish = plan.PublishedUtc is null;

        if (plan.Status != PlanStatus.Published)
        {
            plan.Status = PlanStatus.Published;
            plan.PublishedUtc ??= DateTime.UtcNow;
            await Db.SaveChangesAsync();
        }

        if (firstPublish)
        {
            var planUrl = Url.Action("Details", "Plan",
                new { slug, id = plan.Id }, Request.Scheme)!;

            await _notifications.NotifyPublishedAsync(ctx!.Team, plan, planUrl,
                s => Url.Action("Unsubscribe", "Subscription",
                    new { token = s.UnsubToken }, Request.Scheme)!);
        }

        return RedirectToAction(nameof(EditPlan), new { slug, id });
    }

    [HttpPost("plans/{id:int}/unpublish")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        plan.Status = PlanStatus.Draft;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(EditPlan), new { slug, id });
    }

    [HttpPost("plans/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlan(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        Db.Plans.Remove(plan);
        await Db.SaveChangesAsync();
        _storage.DeletePlan(ctx!.Team.Id, id);

        return RedirectToAction(nameof(Index), new { slug, notice = "Plan deleted." });
    }

    // ── Roster ───────────────────────────────────────────────────────────

    [HttpPost("roster/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPlayer(string slug, string name, string? jerseyNumber)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        if (!string.IsNullOrWhiteSpace(name))
        {
            Db.Players.Add(new Player
            {
                TeamId = ctx!.Team.Id,
                Name = name.Trim(),
                JerseyNumber = string.IsNullOrWhiteSpace(jerseyNumber) ? null : jerseyNumber.Trim()
            });
            await Db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    /// <summary>
    /// Hard delete, not a flag. This is a roster of minors — when someone leaves the team their
    /// name and view history should actually go.
    /// </summary>
    [HttpPost("roster/{playerId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlayer(string slug, int playerId)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var player = await Db.Players
            .FirstOrDefaultAsync(p => p.Id == playerId && p.TeamId == ctx!.Team.Id);
        if (player is null) return NotFound();

        Db.PlanViews.RemoveRange(Db.PlanViews.Where(v => v.PlayerId == playerId));
        Db.Players.Remove(player);
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { slug, notice = $"Removed {player.Name}." });
    }

    // ── Branding and codes ───────────────────────────────────────────────

    [HttpPost("branding")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Branding(string slug, string? name, string? primaryColor,
        string? accentColor, string? timeZoneId, IFormFile? logo)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var team = ctx!.Team;
        if (!string.IsNullOrWhiteSpace(name)) team.Name = name.Trim();
        if (IsHexColor(primaryColor)) team.PrimaryColor = primaryColor!;
        if (IsHexColor(accentColor)) team.AccentColor = accentColor!;
        if (!string.IsNullOrWhiteSpace(timeZoneId)) team.TimeZoneId = timeZoneId.Trim();

        string? notice = null;
        if (logo is { Length: > 0 })
        {
            notice = await SaveLogoAsync(team, logo);
        }

        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { slug, notice = notice ?? "Saved." });
    }

    /// <summary>
    /// Rotates the shared view code. Every player uses the same code, so this is how a coach
    /// cuts off someone who has left the team without waiting on a deploy.
    /// </summary>
    [HttpPost("rotate-code")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateCode(string slug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var fresh = Security.NewAccessCode();
        ctx!.Team.ViewCode = fresh;
        ctx.Team.ViewCodeHash = Security.HashCode(fresh);
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { slug, notice = $"New team code: {fresh}" });
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<string?> SaveLogoAsync(Team team, IFormFile logo)
    {
        if (logo.Length > MaxLogoBytes) return "Logo must be under 2 MB.";

        var contentType = logo.ContentType?.ToLowerInvariant() ?? string.Empty;
        if (!AllowedLogoTypes.Contains(contentType))
            return "Logo must be a JPEG, PNG, GIF or WebP image.";

        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg"
        };

        var dir = _paths.TeamDirectory(team.Id);
        Directory.CreateDirectory(dir);

        // New filename each time so browsers and the CDN don't serve the old logo.
        var fileName = $"logo-{Guid.NewGuid():N}{ext}";
        await using (var destination = System.IO.File.Create(Path.Combine(dir, fileName)))
        await using (var source = logo.OpenReadStream())
            await source.CopyToAsync(destination);

        if (team.LogoFileName is not null)
        {
            try { System.IO.File.Delete(Path.Combine(dir, team.LogoFileName)); }
            catch (IOException) { /* non-fatal: the new logo is already in place */ }
        }

        team.LogoFileName = fileName;
        return null;
    }

    private static bool IsHexColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");

    private static string SafeFileName(string raw)
    {
        var name = Path.GetFileName(raw);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "practice-plan.pdf";
        return name.Length > 200 ? name[^200..] : name;
    }

    /// <summary>Next 6pm at or after tomorrow — a sane default the coach usually just accepts.</summary>
    private static DateTime NextPracticeSlot(string timeZoneId)
    {
        var now = WhenLabel.NowIn(timeZoneId);
        return now.Date.AddDays(1).AddHours(18);
    }
}
