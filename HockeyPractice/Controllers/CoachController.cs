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

    /// <summary>
    /// TempData key for a notice carrying a code, which must not travel in the URL. Same
    /// reasoning as SiteAdminController's: a redirect's query string lands in browser history
    /// and the gateway's access log, and neither is a place to leave an access code.
    /// </summary>
    private const string SecretNoticeKey = "hp:notice";

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, string? notice, string? tag, string? name)
    {
        notice ??= TempData[SecretNoticeKey] as string;

        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var needle = tag?.Trim().ToLowerInvariant();

        var plansQuery = Db.Plans.Include(p => p.Tags).Where(p => p.TeamId == ctx!.Team.Id)
            .MatchingTitle(name);
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
            ActiveName = name,
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
            // No default. A guessed date that happens to be wrong is published as readily as one
            // that is right, and nothing downstream can tell the difference; an empty required
            // field asks the one question only the coach can answer.
            DefaultDate = null,
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
    public async Task<IActionResult> NewPlan(string slug, string title, DateTime? practiceDate,
        string? location, string? coachNotes, IFormFile? file, List<string>? tags,
        PlanKind kind = PlanKind.Pdf)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        // A drill plan has no file to check — its content comes from the library afterwards.
        var error = kind == PlanKind.Pdf ? _storage.ValidateUpload(file) : null;
        error ??= TitleError(title);
        error ??= DateError(practiceDate);

        if (error is not null)
        {
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx!, Error = error, Kind = kind,
                // Whatever they typed, including nothing. Substituting a date here would answer the
                // question the form is holding them on.
                DefaultDate = practiceDate,
                MaxUploadBytes = _storage.QuotaBytes,
                RetainedTitle = title, RetainedLocation = location, RetainedNotes = coachNotes,
                RetainedTags = tags
            });
        }

        var plan = new PracticePlan
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            PracticeDateLocal = practiceDate!.Value,
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

        var saved = await _storage.SaveAsync(ctx.Team.Id, file!);
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
        plan.PdfKey = saved.Key;

        // Extraction is a convenience — if it finds nothing the plan still uploads and renders.
        var extracted = _links.Extract(_storage.ResolvePath(ctx.Team.Id, plan.Id, plan.PdfKey));

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

    // ── Duplicating a plan ───────────────────────────────────────────────

    /// <summary>
    /// The form for a duplicate. A form first, rather than one press that copies the row and drops
    /// the coach into the editor, because the practice date has to be filled in before the plan
    /// exists — a plan with no date would have to be understood by the cards, the print view, the
    /// ordering and the publish email, for a state that would only exist between two clicks.
    ///
    /// Rendered through the EditPlan view in its "new" mode, with the source's wording carried in
    /// the Retained* fields the failed-upload path already uses for exactly this job.
    /// </summary>
    [HttpGet("plans/{id:int}/duplicate")]
    public async Task<IActionResult> DuplicatePlan(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var source = await Db.Plans.Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (source is null) return NotFound();

        ViewBag.NavSection = "manage";
        return View("EditPlan", DuplicateForm(ctx!, source));
    }

    [HttpPost("plans/{id:int}/duplicate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DuplicatePlan(string slug, int id, string title,
        DateTime? practiceDate, string? location, string? coachNotes, List<string>? tags)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        // Re-read rather than trusted from the form: this page may have been open a while, and the
        // plan being copied can have been edited or deleted in the meantime.
        var source = await Db.Plans.Include(p => p.Tags).Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (source is null) return NotFound();

        var error = TitleError(title) ?? DateError(practiceDate);

        // The PDF is settled before anything is written, so a plan is never created and then found
        // to have no document. Kind comes from the row, never from the form's hidden field: trusting
        // that would let a crafted post mint a drill-kind plan carrying a PDF's key, which is the
        // same mismatch ReplaceFile refuses.
        string? pdfKey = null;
        if (error is null && source.Kind == PlanKind.Pdf)
        {
            if (DataPaths.IsPdfKey(source.PdfKey))
            {
                // Already in the store, so the copy just points at the same file. Nothing is
                // written, which is why a full volume is no reason to refuse.
                pdfKey = source.PdfKey;
            }
            else if (_storage.IsFull())
            {
                error = "Storage is nearly full. Delete some old practice plans before copying this one.";
            }
            else
            {
                // A plan from before PDFs were content-addressed. Its file is copied into the store
                // for the new plan and the source is left exactly as it was — duplicating something
                // must not rewrite it. The second copy of this plan costs nothing: the same bytes
                // hash to the same name.
                var adopted = await _storage.AdoptLegacyAsync(ctx!.Team.Id, source.Id);
                if (!adopted.Ok) error = adopted.Error;
                else pdfKey = adopted.Key;
            }
        }

        if (error is not null)
        {
            // What they typed, not what the source says: a bounced submit must not quietly undo an
            // edit they made on the way through.
            ViewBag.NavSection = "manage";
            return View("EditPlan", new PlanEditViewModel
            {
                Ctx = ctx!,
                SourcePlanId = source.Id,
                Kind = source.Kind,
                Error = error,
                DefaultDate = practiceDate,
                RetainedTitle = title,
                RetainedLocation = location,
                RetainedNotes = coachNotes,
                RetainedTags = tags
            });
        }

        var copy = new PracticePlan
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            PracticeDateLocal = practiceDate!.Value,
            Location = location?.Trim(),
            CoachNotes = coachNotes?.Trim(),
            Kind = source.Kind,
            OriginalFileName = source.OriginalFileName,
            ByteSize = source.ByteSize,
            PdfKey = pdfKey,

            // Always a draft, whatever the source was, and with no PublishedUtc. Publish keys
            // "first publish" off that being null, so the copy mails the team once when it is
            // ready rather than never.
            Status = PlanStatus.Draft
        };

        Db.Plans.Add(copy);
        await Db.SaveChangesAsync();

        foreach (var (name, norm) in ParseTags(tags))
            Db.PlanTags.Add(new PlanTag { PracticePlanId = copy.Id, Name = name, NormalizedName = norm });

        if (source.Kind == PlanKind.Drills && source.OverviewFileName is not null)
        {
            // The overview describes THIS practice's layout, the same argument that brings a
            // plan's own drill times across, so it comes with the copy. Bytes are copied rather
            // than shared, so deleting either plan leaves the other's picture intact. A missing
            // source file leaves the copy without one rather than pointing at nothing.
            var copied = _storage.CopyOverview(ctx.Team.Id, source.Id, ctx.Team.Id, copy.Id,
                source.OverviewFileName);

            if (copied is not null)
            {
                copy.OverviewFileName = copied;
                copy.OverviewBytes = source.OverviewBytes;
            }
        }

        if (source.Kind == PlanKind.Drills)
        {
            // Referencing the library, exactly as a plan built by hand does. A drill archived since
            // the source was built still comes across: the source plan shows it, so the copy of that
            // practice should too.
            //
            // ExtraRunTimeMinutes comes too, and has to: it describes how long the drill ran in
            // THIS practice, not what the drill is, so a copy made to run the same session again
            // would otherwise quietly drop the teaching time and report a shorter practice than
            // the one it was copied from.
            var entries = await Db.PlanDrills
                .Where(pd => pd.PracticePlanId == source.Id)
                .OrderBy(pd => pd.SortOrder).ThenBy(pd => pd.Id)
                .ToListAsync();

            foreach (var entry in entries)
            {
                Db.PlanDrills.Add(new PlanDrill
                {
                    PracticePlanId = copy.Id,
                    DrillId = entry.DrillId,
                    SortOrder = entry.SortOrder,
                    ExtraRunTimeMinutes = entry.ExtraRunTimeMinutes
                });
            }
        }
        else
        {
            // WasEditedByCoach travels with the label, and that is the point of copying these rows
            // rather than re-reading the PDF: a name the coach fixed on the original survives a
            // Re-extract on the copy too.
            foreach (var link in source.Links.OrderBy(l => l.SortOrder))
            {
                Db.PlanLinks.Add(new PlanLink
                {
                    PracticePlanId = copy.Id,
                    Url = link.Url,
                    Label = link.Label,
                    Section = link.Section,
                    Kind = link.Kind,
                    VideoId = link.VideoId,
                    VideoTitle = link.VideoTitle,
                    SortOrder = link.SortOrder,
                    IsHidden = link.IsHidden,
                    WasEditedByCoach = link.WasEditedByCoach
                });
            }
        }

        await Db.SaveChangesAsync();
        _log.LogInformation("Plan {PlanId} duplicated as {CopyId} for team {TeamId}",
            source.Id, copy.Id, ctx.Team.Id);

        return RedirectToAction(nameof(EditPlan), new
        {
            slug,
            id = copy.Id,
            notice = "Copied. This is a new draft: nothing you change here touches the plan it came from."
        });
    }

    /// <summary>The duplicate form, filled in from the plan being copied. The date is deliberately
    /// not carried over: last week's date on next week's practice is a mistake waiting to be
    /// published.</summary>
    private static PlanEditViewModel DuplicateForm(TeamContext ctx, PracticePlan source) =>
        new()
        {
            Ctx = ctx,
            SourcePlanId = source.Id,
            Kind = source.Kind,
            DefaultDate = null,
            RetainedTitle = source.Title,
            RetainedLocation = source.Location,
            RetainedNotes = source.CoachNotes,
            RetainedTags = source.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToList()
        };

    [HttpGet("plans/{id:int}")]
    public async Task<IActionResult> EditPlan(string slug, int id, string? notice, string? drillTag,
        string? drillName, int drillPage = 1)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links).Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        // Hoisted out of the initialiser below because the pager needs the totals as well as the
        // rows. A PDF plan has no picker, so it pays for none of this.
        var library = plan.Kind == PlanKind.Drills
            ? await DrillLibraryQuery(ctx!.Team.Id, drillTag, drillName)
                .ToPageAsync(drillPage, DrillController.PageSize)
            : DrillSearch.PagedResult<Drill>.Empty;

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
            Library = library.Items.Select(d => new DrillCard { Drill = d }).ToList(),
            AllDrillTags = plan.Kind == PlanKind.Drills
                ? await DistinctDrillTagsAsync(ctx.Team.Id)
                : new List<string>(),
            ActiveDrillTag = drillTag,
            ActiveDrillName = drillName,
            LibraryPager = new PagerModel
            {
                Page = library.Page,
                TotalPages = library.TotalPages,
                TotalItems = library.TotalItems,
                PageSize = DrillController.PageSize,
                Action = nameof(EditPlan),
                Controller = "Coach",
                PageKey = "drillPage",
                // Anchored at the picker, unlike the POST redirects, which land on the plan's own
                // list. Turning a page is browsing the library, so it should leave you where the
                // library is rather than at the top of a long plan.
                Anchor = "hp-drill-picker",
                RouteValues = new Dictionary<string, string?>
                {
                    ["slug"] = slug,
                    ["id"] = id.ToString(),
                    ["drillTag"] = drillTag,
                    ["drillName"] = drillName
                }
            }
        };

        ViewBag.NavSection = "manage";
        return View(model);
    }

    // ── Building a plan out of drills ────────────────────────────────────

    [HttpPost("plans/{id:int}/drills/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDrill(string slug, int id, int drillId, string? drillTag,
        string? drillName, int drillPage = 1)
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

        return BackToPlan(slug, id, drillTag, drillName, drillPage);
    }

    [HttpPost("plans/{id:int}/drills/{planDrillId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDrill(string slug, int id, int planDrillId, string? drillTag,
        string? drillName, int drillPage = 1)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var entry = await Db.PlanDrills
            .FirstOrDefaultAsync(pd => pd.Id == planDrillId && pd.PracticePlanId == id
                                       && pd.PracticePlan!.TeamId == ctx!.Team.Id);
        if (entry is null) return NotFound();

        Db.PlanDrills.Remove(entry);
        await Db.SaveChangesAsync();

        return BackToPlan(slug, id, drillTag, drillName, drillPage);
    }

    /// <summary>
    /// Moves a drill one place up or down by swapping SortOrder with its neighbour — the same
    /// approach as the site-admin team reorder, which is immune to gaps and ties.
    ///
    /// Swapping rather than renumbering also keeps PlanDrill.Id still, which is what any per-row
    /// state hangs off — ExtraRunTimeMinutes today. A reorder rewritten as delete-and-reinsert
    /// would silently throw that away, so if this ever needs to become a drag-and-drop, move the
    /// rows' SortOrder values and do not recreate the rows.
    /// </summary>
    [HttpPost("plans/{id:int}/drills/{planDrillId:int}/move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveDrill(string slug, int id, int planDrillId,
        string direction, string? drillTag, string? drillName, int drillPage = 1)
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
            return BackToPlan(slug, id, drillTag, drillName, drillPage);

        (ordered[index].SortOrder, ordered[neighbour].SortOrder) =
            (ordered[neighbour].SortOrder, ordered[index].SortOrder);

        await Db.SaveChangesAsync();
        return BackToPlan(slug, id, drillTag, drillName, drillPage);
    }

    /// <summary>
    /// Sets or clears the minutes this plan adds to one drill, so a drill that needs teaching can
    /// run long in this practice without the library drill, or any other plan using it, changing.
    ///
    /// Takes the minutes as a string rather than an int?. Model binding turns "abc" into null,
    /// which here would read as "clear it" and throw the coach's typo away without a word. The
    /// drill form takes it as a string for the same reason.
    /// </summary>
    [HttpPost("plans/{id:int}/drills/{planDrillId:int}/time")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDrillTime(string slug, int id, int planDrillId,
        string? extraMinutes, string? clear, string? drillTag, string? drillName, int drillPage = 1)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        // The drill comes too: its own time is what the new minutes are added to, and its title is
        // what a refusal has to name, since a refusal lands at the top of a long page.
        var entry = await Db.PlanDrills.Include(pd => pd.Drill)
            .FirstOrDefaultAsync(pd => pd.Id == planDrillId && pd.PracticePlanId == id
                                       && pd.PracticePlan!.TeamId == ctx!.Team.Id);
        if (entry is null) return NotFound();

        var title = entry.Drill!.Title;
        var hasBase = entry.Drill.RunTimeMinutes is not null;

        // Clearing the box and saving means the same as pressing Clear: this plan has nothing to
        // say about how long the drill takes, so it goes back to the library time.
        if (clear is not null || string.IsNullOrWhiteSpace(extraMinutes))
        {
            entry.ExtraRunTimeMinutes = null;
            await Db.SaveChangesAsync();
            return BackToPlan(slug, id, drillTag, drillName, drillPage);
        }

        if (!int.TryParse(extraMinutes.Trim(), out var minutes))
        {
            return DrillTimeRefused(slug, id, hasBase
                ? $"\"{title}\": extra time needs to be a number of minutes, like 10."
                : $"\"{title}\": a time for this practice needs to be a number of minutes, like 25.",
                drillTag, drillName, drillPage);
        }

        if (minutes < 1)
        {
            return DrillTimeRefused(slug, id, hasBase
                ? $"\"{title}\": extra time has to be at least 1 minute. To take it off, clear the box and save."
                : $"\"{title}\": a time for this practice has to be at least 1 minute.",
                drillTag, drillName, drillPage);
        }

        // long, not int: int.TryParse happily accepts int.MaxValue, and adding a base to that
        // wraps negative, which would slip past a plain "> MaxMinutes" check as a short drill.
        // The cap is on how long the drill actually runs, so a plan cannot route around the
        // library's limit by adding to it.
        var effective = (long)(entry.Drill.RunTimeMinutes ?? 0) + minutes;
        if (effective > RunTime.MaxMinutes)
        {
            return DrillTimeRefused(slug, id,
                $"\"{title}\": that would make the drill {effective} minutes in this plan. " +
                $"The most a drill can run for is {RunTime.MaxMinutes} minutes.",
                drillTag, drillName, drillPage);
        }

        entry.ExtraRunTimeMinutes = minutes;
        await Db.SaveChangesAsync();

        // No notice on the way back. The redirect lands on the drill list, where the row's own
        // number, its summary line and the plan total have all visibly changed.
        return BackToPlan(slug, id, drillTag, drillName, drillPage);
    }

    /// <summary>
    /// A refused drill time, landing at the TOP of the editor rather than on the drill list.
    ///
    /// Deliberately not BackToPlan. Its #hp-plan-drills anchor is right for every action that
    /// succeeded and wrong here, because the notice renders at the top of the page and an anchored
    /// redirect scrolls straight past the only reason the coach is back on this page. Landing at
    /// the top is what costs them sight of the row, which is why every message names the drill.
    /// The picker's filter and page still ride along, so the library below is where they left it.
    /// </summary>
    private IActionResult DrillTimeRefused(string slug, int id, string notice,
        string? drillTag, string? drillName, int drillPage) =>
        RedirectToAction(nameof(EditPlan), new { slug, id, notice, drillTag, drillName, drillPage });

    [HttpPost("plans/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPlan(string slug, int id, string title,
        DateTime? practiceDate, string? location, string? coachNotes,
        int[]? linkId, string[]? linkLabel, int[]? visibleLinkId, List<string>? tags)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.Include(p => p.Links).Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        // Refused rather than written, and refused before anything else is touched. A blank date
        // used to bind to year 1 and be assigned straight over a good one, which reads on the list
        // as the plan simply vanishing to the bottom.
        if (DateError(practiceDate) is { } dateError)
        {
            return RedirectToAction(nameof(EditPlan), new { slug, id, notice = dateError });
        }

        plan.Title = string.IsNullOrWhiteSpace(title) ? plan.Title : title.Trim();
        plan.PracticeDateLocal = practiceDate!.Value;
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

        if (!_storage.Exists(ctx!.Team.Id, plan.Id, plan.PdfKey))
            return RedirectToAction(nameof(EditPlan), new { slug, id });

        var edited = CoachEdits(plan.Links);

        var fresh = _links.Extract(_storage.ResolvePath(ctx.Team.Id, plan.Id, plan.PdfKey));
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

        // The same preservation ReExtract uses, so replacing the file doesn't undo a correction
        // someone already made.
        var edited = CoachEdits(plan.Links);

        var saved = await _storage.SaveAsync(ctx!.Team.Id, file!);
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

        var previousKey = plan.PdfKey;

        plan.OriginalFileName = SafeFileName(file!.FileName);
        plan.ByteSize = saved.Bytes;
        plan.PdfKey = saved.Key;

        var fresh = _links.Extract(_storage.ResolvePath(ctx.Team.Id, plan.Id, plan.PdfKey));
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

        // The file this plan used to hold, now that nothing about this plan points at it any more.
        await DropUnreferencedPdfAsync(ctx.Team.Id, previousKey, plan.PdfKey);

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

    /// <summary>
    /// Sets or replaces the plan's overview picture — the one showing the shape of the whole
    /// practice, for a session run as simultaneous stations that no running order can describe.
    /// </summary>
    [HttpPost("plans/{id:int}/overview")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> SetOverview(string slug, int id, IFormFile? overview)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        // Drill plans only, the same way ReplaceFile is PDF-only and for the same reason: a stale
        // tab or a crafted POST must not leave a row claiming one kind while carrying the other's
        // content.
        if (plan.Kind != PlanKind.Drills) return NotFound();

        if (overview is null || overview.Length == 0)
            return RedirectToAction(nameof(EditPlan),
                new { slug, id, notice = "Pick a picture first." });

        var saved = await _storage.SaveOverviewAsync(ctx!.Team.Id, plan.Id, overview);
        if (!saved.Ok)
            return RedirectToAction(nameof(EditPlan), new { slug, id, notice = saved.Error });

        var previous = plan.OverviewFileName;

        plan.OverviewFileName = saved.FileName;
        plan.OverviewBytes = saved.Bytes;
        await Db.SaveChangesAsync();

        // Row first, then the old file. An orphaned file wastes quota; a row pointing at a file
        // that no longer exists is a broken picture on every player's plan.
        if (previous is not null && previous != saved.FileName)
            _storage.DeleteOverview(ctx.Team.Id, plan.Id, previous);

        return RedirectToAction(nameof(EditPlan),
            new { slug, id, notice = "Overview picture saved." });
    }

    [HttpPost("plans/{id:int}/overview/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveOverview(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();
        if (plan.OverviewFileName is null) return NotFound();

        var removing = plan.OverviewFileName;

        plan.OverviewFileName = null;
        plan.OverviewBytes = 0;
        await Db.SaveChangesAsync();

        _storage.DeleteOverview(ctx!.Team.Id, plan.Id, removing);

        return RedirectToAction(nameof(EditPlan),
            new { slug, id, notice = "Overview picture removed." });
    }

    [HttpPost("plans/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlan(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();

        var key = plan.PdfKey;

        Db.Plans.Remove(plan);
        await Db.SaveChangesAsync();

        // The legacy directory belongs to this plan alone, so it always goes. The stored PDF may be
        // shared with a duplicate of this plan, so it only goes if nothing is left pointing at it.
        _storage.DeletePlan(ctx!.Team.Id, id);
        await DropUnreferencedPdfAsync(ctx.Team.Id, key, keeping: null);

        return RedirectToAction(nameof(Index), new { slug, notice = "Plan deleted." });
    }

    /// <summary>
    /// Removes a stored PDF once no plan references it any more.
    ///
    /// Called AFTER the change that dropped the reference has been saved, so the rows are the
    /// answer and no "except this one" exclusion is needed. <paramref name="keeping"/> is the key
    /// the caller has just moved TO, and exists for one case that is easy to miss and destructive
    /// when missed: re-uploading a file that hasn't changed hashes to the same key, so the plan's
    /// old key and its new one are the same file, and deleting it would take out the document the
    /// plan is now pointing at.
    ///
    /// Failing to delete costs orphaned bytes, which the storage meter counts and a person can
    /// clear. Deleting one byte too eagerly costs a practice plan.
    /// </summary>
    private async Task DropUnreferencedPdfAsync(int teamId, string? key, string? keeping)
    {
        if (key is null || key == keeping) return;
        if (await Db.Plans.AnyAsync(p => p.TeamId == teamId && p.PdfKey == key)) return;

        _storage.DeleteStoredPdf(teamId, key);
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

        // Out of band, for the same reason as the access codes. The whole point of this action
        // is that the name goes, so putting it in a query string on the way out would leave it
        // in browser history and the gateway log after the row it came from is gone.
        TempData[SecretNoticeKey] = $"Removed {player.Name}.";
        return RedirectToAction(nameof(Index), new { slug });
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

        // Out of band, not in the query string. The team code is shown on this page anyway, so
        // this is the smaller of the two leaks the redirect used to carry, but it is the same
        // leak, into the same log, and it costs nothing to close it here too.
        TempData[SecretNoticeKey] = $"New team code: {fresh}";
        return RedirectToAction(nameof(Index), new { slug });
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
    /// Back to the plan editor, scrolled to the plan's own drill list.
    ///
    /// Always that list, whatever the action was. After adding, moving or removing a drill, what
    /// the coach wants to see is the plan as it now stands, so every one of those lands in the
    /// same place. RedirectToAction can't carry a fragment, and without one they all land at the
    /// top of a long page instead.
    /// </summary>
    /// <summary>
    /// The single redirect every drill action funnels through, so the picker's filters and page
    /// survive an add, a move or a remove in one place rather than four.
    ///
    /// The anchor stays on the plan's own list and not the picker: after adding a drill you want
    /// to see it land in the plan, which is what was asked for. Only the pager's own links point
    /// at the picker.
    /// </summary>
    private IActionResult BackToPlan(string slug, int id, string? drillTag, string? drillName,
        int drillPage) =>
        Redirect(Url.Action(nameof(EditPlan), new { slug, id, drillTag, drillName, drillPage })
                 + "#hp-plan-drills");

    /// <summary>The plan's drills, in order. Ties on SortOrder break on Id so the order is stable.</summary>
    private async Task<List<DrillCard>> PlanDrillsAsync(int planId)
    {
        var entries = await Db.PlanDrills
            .Include(pd => pd.Drill).ThenInclude(d => d!.Diagrams)
            // Tags as well, because _DrillRow renders tag pills and this list feeds it. Without
            // this they were populated only by EF's relationship fix-up from the picker query
            // below, which includes Tags — so a drill's pills showed on the pages of the library
            // picker that happened to contain that same drill and vanished on every other page.
            .Include(pd => pd.Drill).ThenInclude(d => d!.Tags)
            .Where(pd => pd.PracticePlanId == planId)
            .OrderBy(pd => pd.SortOrder).ThenBy(pd => pd.Id)
            .ToListAsync();

        // ExtraRunTimeMinutes is carried here and deliberately NOT on the picker's cards below.
        // Both render through _DrillRow, and that asymmetry is the whole reason a plan's own time
        // cannot show up against the library copy of the same drill.
        return entries.Select(pd => new DrillCard
        {
            Drill = pd.Drill!,
            PlanDrillId = pd.Id,
            ExtraRunTimeMinutes = pd.ExtraRunTimeMinutes,
            EmbedUrl = LinkExtractionService.EmbedUrlFor(pd.Drill!.VideoUrl)
        }).ToList();
    }

    /// <summary>The team's pickable drills — archived ones are deliberately left out.</summary>
    /// <summary>
    /// The pickable library as a query, filtered and ordered but not run, so the caller can take
    /// just the page it needs.
    ///
    /// ThenBy(Id) is load-bearing once this is paged: Title alone is not a total order, and
    /// Skip/Take over an ambiguous sort can serve one drill on two pages and never serve another.
    /// </summary>
    private IQueryable<Drill> DrillLibraryQuery(int teamId, string? tag, string? name) =>
        Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .Where(d => d.TeamId == teamId && !d.IsArchived)
            .MatchingTag(tag)
            .MatchingName(name)
            .OrderBy(d => d.Title)
            .ThenBy(d => d.Id);

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

    private static string? TitleError(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Give the plan a title." : null;

    /// <summary>
    /// A practice with no date is not a practice, so there is no default to fall back on — the form
    /// opens blank and this is what holds the line behind it. The field is marked required, but that
    /// is the browser's promise, not ours: a stale tab, a posted form or a removed attribute all
    /// reach the action with nothing, and DateTime binding turns nothing into year 1 rather than an
    /// error. Binding as DateTime? is what makes "they left it blank" distinguishable at all.
    /// </summary>
    private static string? DateError(DateTime? practiceDate) =>
        practiceDate is null || practiceDate.Value == default
            ? "Pick a date and time for the practice."
            : null;

    public const int MaxTags = 15;

    private static string NormalizeTag(string name) =>
        string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static List<(string Name, string Normalized)> ParseTags(List<string>? raw)
    {
        if (raw is null) return [];

        var seen = new HashSet<string>();
        var result = new List<(string, string)>();

        // Tags arrive as discrete values now, one per chip, rather than one delimited string.
        // That removes the delimiter entirely — a tag containing a comma used to be silently
        // split in two — but everything else still has to hold: trim, cap the length, drop
        // blanks, and dedupe case-insensitively keeping the casing that came first.
        foreach (var piece in raw)
        {
            var trimmed = (piece ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length > 40) trimmed = trimmed[..40];

            var norm = NormalizeTag(trimmed);
            if (norm.Length == 0 || !seen.Add(norm)) continue;

            result.Add((trimmed, norm));
            if (result.Count >= MaxTags) break;
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

    /// <summary>
    /// The coach's own wording and visibility choices, keyed by URL, so re-reading the PDF or
    /// swapping the file for a new one doesn't undo a correction someone already made.
    ///
    /// Grouped before the dictionary is built, and that is load-bearing rather than tidy.
    /// Extraction deliberately keeps two cards for one video when the document names them
    /// differently (a warm-up clip linked from three drill rows is three cards a player looks
    /// for by name), so <c>plan.Links</c> legitimately holds several rows with the same Url.
    /// Once a coach relabels more than one of them, a plain ToDictionary throws on the duplicate
    /// key and takes down both Re-extract and Replace file for that plan, permanently and with
    /// nothing on screen saying why.
    ///
    /// The consequence of keying on the URL at all is that duplicates cannot be told apart: the
    /// first edit wins and every fresh card for that URL gets its label. That is a real loss of
    /// precision in a rare case, and it is the right trade against a dead button: the coach can
    /// still relabel the others afterwards, which is what they did the first time.
    /// </summary>
    private static Dictionary<string, (string Label, bool IsHidden)> CoachEdits(
        IEnumerable<PlanLink> links) =>
        links.Where(l => l.WasEditedByCoach)
            .GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(l => l.Url, l => (l.Label, l.IsHidden), StringComparer.OrdinalIgnoreCase);

    private static string SafeFileName(string raw)
    {
        var name = Path.GetFileName(raw);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "practice-plan.pdf";
        return name.Length > 200 ? name[^200..] : name;
    }
}
