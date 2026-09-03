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
    public async Task<IActionResult> Index(string slug, string? notice, string? tag)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var needle = tag?.Trim().ToLowerInvariant();

        var plansQuery = Db.Plans.Include(p => p.Tags).Where(p => p.TeamId == ctx!.Team.Id);
        if (!string.IsNullOrEmpty(needle))
            plansQuery = plansQuery.Where(p => p.Tags.Any(t => t.NormalizedName.Contains(needle)));

        var plans = await plansQuery
            .Select(p => new
            {
                Plan = p,
                Videos = p.Links.Count(l => !l.IsHidden),
                Drills = p.Drills.Count
            })
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
                DrillCount = x.Drills,
                WhenLabel = WhenLabel.For(x.Plan.PracticeDateLocal, ctx!.Team.TimeZoneId)
            }).ToList(),
            Roster = await Db.Players.Where(p => p.TeamId == ctx!.Team.Id)
                        .OrderBy(p => p.Name).ToListAsync(),
            ConfirmedSubscribers = await Db.Subscribers
                        .CountAsync(s => s.TeamId == ctx!.Team.Id && s.ConfirmedUtc != null),
            UsedBytes = _storage.UsedBytes(),
            QuotaBytes = _storage.QuotaBytes,
            AllTags = await DistinctTagsAsync(ctx!.Team.Id),
            ActiveTag = tag,
            Notice = notice
        });
    }

    // ── Plans ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks which kind of plan this is before showing a form, because the two need different
    /// fields. Two plain links rather than a JavaScript toggle: nothing to mis-toggle, and the
    /// file input is simply absent for a drill plan rather than hidden-but-still-required.
    /// </summary>
    [HttpGet("plans/choose")]
    public async Task<IActionResult> ChoosePlanKind(string slug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        ViewBag.NavSection = "manage";
        return View(ctx!);
    }

    [HttpGet("plans/new")]
    public async Task<IActionResult> NewPlan(string slug, PlanKind kind = PlanKind.Pdf)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        ViewBag.NavSection = "manage";
        return View("EditPlan", new PlanEditViewModel
        {
            Ctx = ctx!,
            Kind = kind,
            DefaultDate = NextPracticeSlot(ctx!.Team.TimeZoneId),
            MaxUploadBytes = _storage.QuotaBytes,
            // Only a PDF plan is blocked by a full volume at this point; a drill plan writes
            // nothing until a diagram is added.
            Error = kind == PlanKind.Pdf && _storage.IsFull()
                ? "Storage is nearly full. Delete some old plans before uploading a new one."
                : null
        });
    }

    [HttpPost("plans/new")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> NewPlan(string slug, string title, DateTime practiceDate,
        string? location, string? coachNotes, IFormFile? file, string? tags,
        PlanKind kind = PlanKind.Pdf)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        // A drill plan has no file to check — its content comes from the library afterwards.
        var error = kind == PlanKind.Pdf ? _storage.ValidateUpload(file) : null;
        if (string.IsNullOrWhiteSpace(title)) error ??= "Give the plan a title.";

        if (error is not null)
        {
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx!, Error = error, Kind = kind,
                // A default(DateTime) from failed binding renders as year 1 — fall back.
                DefaultDate = practiceDate == default ? NextPracticeSlot(ctx!.Team.TimeZoneId) : practiceDate,
                MaxUploadBytes = _storage.QuotaBytes,
                RetainedTitle = title, RetainedLocation = location, RetainedNotes = coachNotes,
                RetainedTags = tags
            });
        }

        var plan = new PracticePlan
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            PracticeDateLocal = practiceDate,
            Location = location?.Trim(),
            CoachNotes = coachNotes?.Trim(),
            Kind = kind,
            OriginalFileName = kind == PlanKind.Pdf ? SafeFileName(file!.FileName) : null,
            Status = PlanStatus.Draft
        };

        // Saved first so the plan has an id to key its directory off.
        Db.Plans.Add(plan);
        await Db.SaveChangesAsync();

        if (kind == PlanKind.Drills)
        {
            foreach (var (name, norm) in ParseTags(tags))
                Db.PlanTags.Add(new PlanTag { PracticePlanId = plan.Id, Name = name, NormalizedName = norm });

            await Db.SaveChangesAsync();
            return RedirectToAction(nameof(EditPlan), new { slug, id = plan.Id });
        }

        var saved = await _storage.SaveAsync(ctx.Team.Id, plan.Id, file!);
        if (!saved.Ok)
        {
            Db.Plans.Remove(plan);
            await Db.SaveChangesAsync();
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx, DefaultDate = practiceDate, Error = saved.Error, Kind = kind,
                MaxUploadBytes = _storage.QuotaBytes,
                RetainedTitle = title, RetainedLocation = location, RetainedNotes = coachNotes,
                RetainedTags = tags
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

        foreach (var (name, norm) in ParseTags(tags))
            Db.PlanTags.Add(new PlanTag { PracticePlanId = plan.Id, Name = name, NormalizedName = norm });

        await Db.SaveChangesAsync();
        _log.LogInformation("Plan {PlanId} uploaded for team {TeamId} ({Bytes} bytes)",
            plan.Id, ctx.Team.Id, saved.Bytes);

        return RedirectToAction(nameof(EditPlan), new { slug, id = plan.Id });
    }

    [HttpGet("plans/{id:int}")]
    public async Task<IActionResult> EditPlan(string slug, int id, string? notice, string? drillTag)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links).Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        var model = new PlanEditViewModel
        {
            Ctx = ctx!,
            Plan = plan,
            Kind = plan.Kind,
            Links = plan.Links.OrderBy(l => l.SortOrder).ToList(),
            DefaultDate = plan.PracticeDateLocal,
            MaxUploadBytes = _storage.QuotaBytes,
            AllTags = await DistinctTagsAsync(ctx!.Team.Id),
            Notice = notice,
            PlanDrills = plan.Kind == PlanKind.Drills
                ? await PlanDrillsAsync(plan.Id)
                : new List<DrillCard>(),
            Library = plan.Kind == PlanKind.Drills
                ? await DrillLibraryAsync(ctx.Team.Id, drillTag)
                : new List<DrillCard>(),
            AllDrillTags = plan.Kind == PlanKind.Drills
                ? await DistinctDrillTagsAsync(ctx.Team.Id)
                : new List<string>(),
            ActiveDrillTag = drillTag
        };

        ViewBag.NavSection = "manage";
        return View(model);
    }

    // ── Building a plan out of drills ────────────────────────────────────

    [HttpPost("plans/{id:int}/drills/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDrill(string slug, int id, int drillId, string? drillTag)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        // The drill must belong to this team — a plan can't borrow another team's library.
        var drill = await Db.Drills.FirstOrDefaultAsync(d => d.Id == drillId && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        var next = await Db.PlanDrills.Where(pd => pd.PracticePlanId == plan.Id)
            .Select(pd => (int?)pd.SortOrder).MaxAsync() ?? -1;

        Db.PlanDrills.Add(new PlanDrill
        {
            PracticePlanId = plan.Id,
            DrillId = drill.Id,
            SortOrder = next + 1
        });
        await Db.SaveChangesAsync();

        // Back to the same row in the library, not the top of the page. Building a plan means
        // adding several drills in a row, and being thrown back to the top after each one means
        // scrolling to find your place again every single time.
        return BackToPlan(slug, id, drillTag, $"lib-{drill.Id}");
    }

    [HttpPost("plans/{id:int}/drills/{planDrillId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDrill(string slug, int id, int planDrillId, string? drillTag)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var entry = await Db.PlanDrills
            .FirstOrDefaultAsync(pd => pd.Id == planDrillId && pd.PracticePlanId == id
                                       && pd.PracticePlan!.TeamId == ctx!.Team.Id);
        if (entry is null) return NotFound();

        Db.PlanDrills.Remove(entry);
        await Db.SaveChangesAsync();

        // The row itself is gone, so aim at the list rather than a now-missing anchor.
        return BackToPlan(slug, id, drillTag, "hp-plan-drills");
    }

    /// <summary>
    /// Moves a drill one place up or down by swapping SortOrder with its neighbour — the same
    /// approach as the site-admin team reorder, which is immune to gaps and ties.
    /// </summary>
    [HttpPost("plans/{id:int}/drills/{planDrillId:int}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveDrill(string slug, int id, int planDrillId,
        string direction, string? drillTag)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        var ordered = await Db.PlanDrills.Where(pd => pd.PracticePlanId == plan.Id)
            .OrderBy(pd => pd.SortOrder).ThenBy(pd => pd.Id)
            .ToListAsync();

        var index = ordered.FindIndex(pd => pd.Id == planDrillId);
        if (index < 0) return NotFound();

        var neighbour = direction == "up" ? index - 1 : index + 1;
        if (neighbour < 0 || neighbour >= ordered.Count)
            return BackToPlan(slug, id, drillTag, $"pd-{planDrillId}");

        (ordered[index].SortOrder, ordered[neighbour].SortOrder) =
            (ordered[neighbour].SortOrder, ordered[index].SortOrder);

        await Db.SaveChangesAsync();

        // Follow the row that moved, so the coach's eye stays on it through a run of reorders.
        return BackToPlan(slug, id, drillTag, $"pd-{planDrillId}");
    }

    [HttpPost("plans/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPlan(string slug, int id, string title,
        DateTime practiceDate, string? location, string? coachNotes,
        int[]? linkId, string[]? linkLabel, int[]? visibleLinkId, string? tags)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links).Include(p => p.Tags)
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

        var parsed = ParseTags(tags);
        var parsedNorms = parsed.Select(p => p.Normalized).ToHashSet();

        // Diff rather than delete-all-and-reinsert: a coach usually edits tags by adding one to
        // an existing set, not rewriting the whole list, so most saves have old and new rows
        // sharing a NormalizedName. Deleting and re-adding those in the same SaveChanges call
        // risks the delete and insert landing in an order that trips the
        // {PracticePlanId, NormalizedName} unique index — EF Core doesn't guarantee
        // delete-before-insert for unrelated sibling rows with no FK between them. Only touching
        // what actually changed makes that collision impossible, since a row that stays tagged
        // is never removed in the first place.
        Db.PlanTags.RemoveRange(plan.Tags.Where(t => !parsedNorms.Contains(t.NormalizedName)));

        var existingNorms = plan.Tags.Select(t => t.NormalizedName).ToHashSet();
        foreach (var (name, norm) in parsed)
            if (!existingNorms.Contains(norm))
                Db.PlanTags.Add(new PlanTag { PracticePlanId = plan.Id, Name = name, NormalizedName = norm });

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

        // PDF-only: there is nothing to re-read on a drill plan.
        if (plan.Kind != PlanKind.Pdf) return NotFound();

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

        // PDF-only. Without this, a stale tab or a crafted POST could bolt a PDF onto a drill
        // plan, leaving a row that claims to be one kind while carrying the other's content.
        if (plan.Kind != PlanKind.Pdf) return NotFound();

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

        // A PDF plan can't be empty — the upload is required. A drill plan can, and publishing one
        // would email the whole team a link to a blank page.
        if (plan.Kind == PlanKind.Drills &&
            !await Db.PlanDrills.AnyAsync(pd => pd.PracticePlanId == plan.Id))
        {
            return RedirectToAction(nameof(EditPlan), new
            {
                slug, id,
                notice = "Add at least one drill before publishing this plan."
            });
        }

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
    /// Sets or clears the team's shared Spotify playlist link. A single removable value, so
    /// this uses direct-overwrite semantics (like a plan's Location/CoachNotes) rather than
    /// Branding's "only touch it if non-empty" partial-update style — clearing the box and
    /// hitting Save has one obvious meaning here.
    ///
    /// An invalid non-empty submission is rejected rather than silently dropped: unlike a bad
    /// hex color, a broken playlist link isn't visually self-evident, so a manager needs to be
    /// told the paste didn't take rather than left wondering why nothing changed.
    /// </summary>
    [HttpPost("playlist")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Playlist(string slug, string? spotifyPlaylistUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var team = ctx!.Team;
        var trimmed = spotifyPlaylistUrl?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            team.SpotifyPlaylistUrl = null;
            await Db.SaveChangesAsync();
            return RedirectToAction(nameof(Index), new { slug, notice = "Playlist link removed." });
        }

        if (!IsSpotifyPlaylistUrl(trimmed))
        {
            return RedirectToAction(nameof(Index), new
            {
                slug,
                notice = "That doesn't look like an open.spotify.com playlist link. Nothing was changed."
            });
        }

        team.SpotifyPlaylistUrl = trimmed;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { slug, notice = "Playlist link saved." });
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

    // Same pattern as Team.SpotifyPlaylistRegex — duplicated rather than shared, matching this
    // file's existing IsHexColor/SafeColor split.
    private static readonly System.Text.RegularExpressions.Regex SpotifyPlaylistUrlPattern =
        new(@"^https://open\.spotify\.com/(intl-[a-z]{2}/)?playlist/[A-Za-z0-9]+(\?\S*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsSpotifyPlaylistUrl(string value) =>
        SpotifyPlaylistUrlPattern.IsMatch(value);

    /// <summary>
    /// Back to the plan editor, scrolled to the thing that was just acted on.
    ///
    /// RedirectToAction can't carry a fragment, and without one every add, move or remove lands
    /// at the top of a long page — which is precisely the page a coach spends the most time
    /// scrolling through.
    /// </summary>
    private IActionResult BackToPlan(string slug, int id, string? drillTag, string anchor) =>
        Redirect(Url.Action(nameof(EditPlan), new { slug, id, drillTag }) + "#" + anchor);

    /// <summary>The plan's drills, in order. Ties on SortOrder break on Id so the order is stable.</summary>
    private async Task<List<DrillCard>> PlanDrillsAsync(int planId)
    {
        var entries = await Db.PlanDrills
            .Include(pd => pd.Drill).ThenInclude(d => d!.Diagrams)
            .Where(pd => pd.PracticePlanId == planId)
            .OrderBy(pd => pd.SortOrder).ThenBy(pd => pd.Id)
            .ToListAsync();

        return entries.Select(pd => new DrillCard
        {
            Drill = pd.Drill!,
            PlanDrillId = pd.Id,
            EmbedUrl = LinkExtractionService.EmbedUrlFor(pd.Drill!.VideoUrl)
        }).ToList();
    }

    /// <summary>The team's pickable drills — archived ones are deliberately left out.</summary>
    private async Task<List<DrillCard>> DrillLibraryAsync(int teamId, string? tag)
    {
        var needle = tag?.Trim().ToLowerInvariant();

        var query = Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .Where(d => d.TeamId == teamId && !d.IsArchived);

        if (!string.IsNullOrEmpty(needle))
            query = query.Where(d => d.Tags.Any(t => t.NormalizedName.Contains(needle)));

        var drills = await query.OrderBy(d => d.Title).ToListAsync();
        return drills.Select(d => new DrillCard { Drill = d }).ToList();
    }

    /// <summary>
    /// Distinct drill-tag names for the team. Grouped in memory rather than with EF GroupBy, whose
    /// "first row per group" translation is fragile on SQLite.
    /// </summary>
    private async Task<List<string>> DistinctDrillTagsAsync(int teamId)
    {
        var rows = await Db.DrillTags
            .Where(t => t.Drill!.TeamId == teamId && !t.Drill.IsArchived)
            .Select(t => new { t.Id, t.Name, t.NormalizedName })
            .ToListAsync();

        return rows.GroupBy(t => t.NormalizedName)
            .Select(g => g.OrderBy(t => t.Id).First().Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private const int MaxTagsPerPlan = 15;

    private static string NormalizeTag(string name) =>
        string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static List<(string Name, string Normalized)> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var seen = new HashSet<string>();
        var result = new List<(string, string)>();
        foreach (var piece in raw.Split(','))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length > 40) trimmed = trimmed[..40];
            var norm = NormalizeTag(trimmed);
            if (norm.Length == 0 || !seen.Add(norm)) continue;   // dedupe, keep first-seen casing
            result.Add((trimmed, norm));
            if (result.Count >= MaxTagsPerPlan) break;
        }
        return result;
    }

    /// <summary>
    /// Distinct tag names for a team, one representative (first-seen) casing per normalized
    /// form, sorted. Grouped in memory rather than via EF GroupBy — SQLite's translator for
    /// "first row per group" is fragile, and a team realistically has a few dozen tag rows
    /// total, so pulling a flat projection and grouping client-side is simpler and just as fast.
    /// </summary>
    private async Task<List<string>> DistinctTagsAsync(int teamId)
    {
        var rows = await Db.PlanTags
            .Where(t => t.PracticePlan!.TeamId == teamId)
            .Select(t => new { t.Id, t.Name, t.NormalizedName })
            .ToListAsync();

        return rows.GroupBy(t => t.NormalizedName)
            .Select(g => g.OrderBy(t => t.Id).First().Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
