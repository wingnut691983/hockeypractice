using HockeyPractice.Persistence;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

/// <summary>
/// The team's drill library: the reusable pieces a practice plan is built from.
///
/// Manager-only, except for serving a diagram — players need that to read a published plan.
/// </summary>
[Route("t/{slug}/drills")]
public class DrillController : TeamScopedController
{
    private readonly PlanStorageService _storage;
    private readonly ILogger<DrillController> _log;

    public DrillController(AppDbContext db, TeamAccessService access, PlanStorageService storage,
        ILogger<DrillController> log)
        : base(db, access)
    {
        _storage = storage;
        _log = log;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, string? tag, bool archived = false,
        string? notice = null)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drills = await QueryLibraryAsync(ctx!.Team.Id, tag, archived);

        ViewBag.NavSection = "drills";
        return View(new DrillListViewModel
        {
            Ctx = ctx,
            Drills = drills,
            AllTags = await DistinctTagsAsync(ctx.Team.Id),
            ActiveTag = tag,
            ShowingArchived = archived,
            CopyTargets = await CopyTargetsAsync(ctx.Team.Id),
            Notice = notice
        });
    }

    [HttpGet("new")]
    public async Task<IActionResult> New(string slug, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        ViewBag.NavSection = "drills";
        return View("Edit", new DrillEditViewModel
        {
            Ctx = ctx!,
            AllTags = await DistinctTagsAsync(ctx!.Team.Id),
            ReturnUrl = returnUrl
        });
    }

    [HttpPost("new")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Create(string slug, string title, string? description,
        string? videoUrl, string? runTimeMinutes, string? tags, List<IFormFile>? diagrams,
        string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var error = ValidateFields(title, videoUrl, runTimeMinutes);
        if (error is not null)
            return await RedisplayAsync(ctx!, null, error, title, description, videoUrl,
                runTimeMinutes, tags, returnUrl);

        var drill = new Drill
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            VideoUrl = string.IsNullOrWhiteSpace(videoUrl) ? null : videoUrl.Trim(),
            RunTimeMinutes = ParseRunTime(runTimeMinutes)
        };

        // Saved first so the drill has an id to key its directory off — same two-phase write the
        // plan upload uses.
        Db.Drills.Add(drill);
        await Db.SaveChangesAsync();

        foreach (var (name, norm) in ParseTags(tags))
            Db.DrillTags.Add(new DrillTag { DrillId = drill.Id, Name = name, NormalizedName = norm });

        var notice = "Drill saved.";
        var rejected = await AddDiagramsAsync(ctx.Team.Id, drill, diagrams);
        if (rejected is not null)
        {
            // Keep the drill rather than discarding everything the coach typed over a bad file —
            // unlike a plan, a drill is perfectly useful without a diagram.
            notice = $"Drill saved, but {rejected}";
        }

        await Db.SaveChangesAsync();
        _log.LogInformation("Drill {DrillId} created for team {TeamId}", drill.Id, ctx.Team.Id);

        if (TryReturnUrlRedirect(returnUrl, out var back)) return back;
        return RedirectToAction(nameof(Index), new { slug, notice });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Edit(string slug, int id, string? notice)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        ViewBag.NavSection = "drills";
        return View(new DrillEditViewModel
        {
            Ctx = ctx!,
            Drill = drill,
            AllTags = await DistinctTagsAsync(ctx!.Team.Id),
            UsedInPlans = await Db.PlanDrills.CountAsync(pd => pd.DrillId == drill.Id),
            Notice = notice
        });
    }

    [HttpPost("{id:int}")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Update(string slug, int id, string title, string? description,
        string? videoUrl, string? runTimeMinutes, string? tags, List<IFormFile>? diagrams)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        var error = ValidateFields(title, videoUrl, runTimeMinutes);
        if (error is not null)
            return await RedisplayAsync(ctx!, drill, error, title, description, videoUrl,
                runTimeMinutes, tags, null);

        drill.Title = title.Trim();
        drill.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        drill.VideoUrl = string.IsNullOrWhiteSpace(videoUrl) ? null : videoUrl.Trim();
        drill.RunTimeMinutes = ParseRunTime(runTimeMinutes);

        SyncTags(drill, tags);

        var notice = $"Saved \"{drill.Title}\".";
        // New pictures ADD to the drill now rather than replacing what's there — a progression
        // builds up over several visits. Removing one is its own explicit action.
        var rejected = await AddDiagramsAsync(ctx!.Team.Id, drill, diagrams);
        var diagramFailed = rejected is not null;
        if (diagramFailed) notice = $"Saved, but {rejected}";

        await Db.SaveChangesAsync();

        // Saving is the end of editing, so go back to the library — the same place Archive and
        // Delete already land, and the drill is right there to confirm the change took.
        // The exception is a rejected diagram: bouncing away would leave the coach re-finding the
        // drill to try another picture, so that one stays put with the reason on screen.
        return diagramFailed
            ? RedirectToAction(nameof(Edit), new { slug, id, notice })
            : RedirectToAction(nameof(Index), new { slug, notice });
    }

    /// <summary>
    /// Detaches one picture from a drill. Its own action rather than part of saving, because
    /// uploads now add to a drill instead of replacing what's there — so removing has to be
    /// something the coach asks for explicitly.
    /// </summary>
    [HttpPost("{id:int}/diagram/{diagramId:int}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDiagram(string slug, int id, int diagramId)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var diagram = await Db.DrillDiagrams
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.DrillId == id
                                      && d.Drill!.TeamId == ctx!.Team.Id);
        if (diagram is null) return NotFound();

        Db.DrillDiagrams.Remove(diagram);
        await Db.SaveChangesAsync();

        // Row first, then the file: an orphaned file wastes quota, but a row pointing at a file
        // that no longer exists renders as a broken image on the players' plan.
        _storage.DeleteDiagram(ctx!.Team.Id, id, diagram.FileName);

        return RedirectToAction(nameof(Edit), new { slug, id, notice = "Picture removed." });
    }

    [HttpPost("{id:int}/archive")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Archive(string slug, int id) => SetArchivedAsync(slug, id, true);

    [HttpPost("{id:int}/unarchive")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Unarchive(string slug, int id) => SetArchivedAsync(slug, id, false);

    /// <summary>
    /// Removes a drill outright. Refused while any plan still uses it — that would tear content
    /// out of a plan already published to the team, so archiving is offered instead.
    /// </summary>
    [HttpPost("{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        var used = await Db.PlanDrills.CountAsync(pd => pd.DrillId == drill.Id);
        if (used > 0)
        {
            return RedirectToAction(nameof(Edit), new
            {
                slug, id,
                notice = $"\"{drill.Title}\" is used in {used} plan{(used == 1 ? "" : "s")}, so it " +
                         "can't be deleted. Archive it instead — it stays in those plans but stops " +
                         "showing up when you build a new one."
            });
        }

        Db.Drills.Remove(drill);
        await Db.SaveChangesAsync();
        _storage.DeleteDrill(ctx!.Team.Id, drill.Id);

        return RedirectToAction(nameof(Index), new { slug, notice = $"Deleted \"{drill.Title}\"." });
    }

    /// <summary>Copies one drill into another team this browser also manages.</summary>
    [HttpPost("{id:int}/copy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Copy(string slug, int id, string targetSlug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        var target = await ResolveCopyTargetAsync(targetSlug);
        if (target is null)
            return RedirectToAction(nameof(Index), new { slug, notice = "You don't manage that team on this device." });

        var existing = await Db.Drills.Where(d => d.TeamId == target.Id)
            .Select(d => d.Title).ToListAsync();
        if (existing.Contains(drill.Title, StringComparer.OrdinalIgnoreCase))
        {
            return RedirectToAction(nameof(Index), new
            {
                slug,
                notice = $"{target.Name} already has a drill called \"{drill.Title}\" — nothing copied."
            });
        }

        await CopyDrillAsync(drill, ctx!.Team.Id, target.Id);
        await Db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new
        {
            slug, notice = $"Copied \"{drill.Title}\" to {target.Name}."
        });
    }

    /// <summary>
    /// Copies the whole library to another team — the end-of-season rollover. Skips drills the
    /// target already has by title, so running it twice is safe and finishing an interrupted run
    /// just works.
    /// </summary>
    [HttpPost("copy-all")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyAll(string slug, string targetSlug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var target = await ResolveCopyTargetAsync(targetSlug);
        if (target is null)
            return RedirectToAction(nameof(Index), new { slug, notice = "You don't manage that team on this device." });

        if (_storage.IsFull())
            return RedirectToAction(nameof(Index), new { slug, notice = "Storage is nearly full — nothing was copied." });

        var source = await Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .Where(d => d.TeamId == ctx!.Team.Id && !d.IsArchived)
            .OrderBy(d => d.Title)
            .ToListAsync();

        // Compared in memory: OrdinalIgnoreCase has no SQL translation and would throw at runtime.
        var existing = await Db.Drills.Where(d => d.TeamId == target.Id)
            .Select(d => d.Title).ToListAsync();
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        int copied = 0, skipped = 0;
        var ranOutOfSpace = false;

        foreach (var drill in source)
        {
            if (!taken.Add(drill.Title)) { skipped++; continue; }

            if (_storage.IsFull()) { ranOutOfSpace = true; break; }

            await CopyDrillAsync(drill, ctx!.Team.Id, target.Id);
            await Db.SaveChangesAsync();
            copied++;
        }

        var notice = $"Copied {copied} drill{(copied == 1 ? "" : "s")} to {target.Name}" +
                     (skipped > 0 ? $", skipped {skipped} already there" : "") + ".";
        if (ranOutOfSpace)
            notice += " Stopped early — storage is full. Free some space and run it again to finish.";

        return RedirectToAction(nameof(Index), new { slug, notice });
    }

    /// <summary>
    /// Serves a drill's diagram. Player level, because players need it to read a published plan —
    /// and ownership-checked, so one team's slug can never reach another team's drill.
    /// </summary>
    [HttpGet("{id:int}/diagram/{diagramId:int}")]
    public async Task<IActionResult> Diagram(string slug, int id, int diagramId)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        // Joined through the drill so a diagram id from another team can't be fetched by
        // pairing it with a slug the viewer does have access to.
        var diagram = await Db.DrillDiagrams
            .FirstOrDefaultAsync(d => d.Id == diagramId && d.DrillId == id
                                      && d.Drill!.TeamId == ctx!.Team.Id);
        if (diagram is null) return NotFound();
        if (!_storage.DiagramExists(ctx!.Team.Id, id, diagram.FileName)) return NotFound();

        var path = _storage.DiagramPath(ctx.Team.Id, id, diagram.FileName);
        var contentType = diagram.IsPdf ? "application/pdf" : "image/webp";

        // PhysicalFile, not File(stream): it sets ETag and Last-Modified and handles range
        // requests, so a diagram isn't re-downloaded on every page view.
        return PhysicalFile(path, contentType);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<IActionResult> SetArchivedAsync(string slug, int id, bool archived)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        drill.IsArchived = archived;
        await Db.SaveChangesAsync();

        // Both directions land on the working library: after archiving, the drill has left it,
        // which is the confirmation; after unarchiving, it's there again.
        return RedirectToAction(nameof(Index), new
        {
            slug,
            notice = archived
                ? $"Archived \"{drill.Title}\". It still shows in plans that already use it."
                : $"\"{drill.Title}\" is back in the library."
        });
    }

    /// <summary>Duplicates a drill, its tags, and its diagram file into another team.</summary>
    private async Task CopyDrillAsync(Drill drill, int fromTeamId, int toTeamId)
    {
        var copy = new Drill
        {
            TeamId = toTeamId,
            Title = drill.Title,
            Description = drill.Description,
            VideoUrl = drill.VideoUrl,
            RunTimeMinutes = drill.RunTimeMinutes,
            CopiedFromDrillId = drill.Id
        };

        Db.Drills.Add(copy);
        await Db.SaveChangesAsync();   // need the new id before writing its directory

        foreach (var tag in drill.Tags)
            Db.DrillTags.Add(new DrillTag
            {
                DrillId = copy.Id, Name = tag.Name, NormalizedName = tag.NormalizedName
            });

        foreach (var diagram in drill.Diagrams.OrderBy(d => d.Id))
        {
            var copied = _storage.CopyDiagram(fromTeamId, drill.Id, toTeamId, copy.Id, diagram.FileName);
            if (copied is not null)
                Db.DrillDiagrams.Add(new DrillDiagram
                {
                    DrillId = copy.Id, FileName = copied, Bytes = diagram.Bytes
                });
        }
    }

    /// <summary>
    /// The team a copy is bound for. Resolved from this browser's own manager claims rather than
    /// the submitted form, so a forged slug can't push drills into a team the user doesn't manage.
    /// </summary>
    private async Task<Team?> ResolveCopyTargetAsync(string? targetSlug)
    {
        if (string.IsNullOrWhiteSpace(targetSlug)) return null;

        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == targetSlug);
        if (team is null) return null;

        return Access.RealLevelFor(User, team.Id) >= TeamAccessLevel.Manager ? team : null;
    }

    /// <summary>
    /// Other teams this browser holds MANAGER access to — the only places a drill can be copied.
    ///
    /// Read from the ticket's own claims, not from a form. Since there are no user accounts, this
    /// is per browser rather than per person: a coach who entered one team's manager code here and
    /// another's on their phone sees no targets, which is why the view explains how to add one
    /// instead of showing an empty dropdown.
    /// </summary>
    private async Task<List<TeamLink>> CopyTargetsAsync(int currentTeamId)
    {
        var managedIds = User.Claims
            .Where(c => c.Type.StartsWith(TeamAccessService.TeamClaimPrefix, StringComparison.Ordinal)
                        && c.Value == "coach")
            .Select(c => int.TryParse(c.Type[TeamAccessService.TeamClaimPrefix.Length..], out var id) ? id : -1)
            .Where(id => id > 0 && id != currentTeamId)
            .ToList();

        if (managedIds.Count == 0) return new List<TeamLink>();

        return await Db.Teams
            .Where(t => managedIds.Contains(t.Id))
            .OrderBy(t => t.Name)
            .Select(t => new TeamLink(t.Slug, t.Name))
            .ToListAsync();
    }

    private async Task<List<DrillCard>> QueryLibraryAsync(int teamId, string? tag, bool archived)
    {
        var needle = tag?.Trim().ToLowerInvariant();

        var query = Db.Drills.Include(d => d.Tags).Include(d => d.Diagrams)
            .Where(d => d.TeamId == teamId && d.IsArchived == archived);

        if (!string.IsNullOrEmpty(needle))
            query = query.Where(d => d.Tags.Any(t => t.NormalizedName.Contains(needle)));

        var drills = await query.OrderBy(d => d.Title).ToListAsync();

        return drills.Select(d => new DrillCard
        {
            Drill = d,
            EmbedUrl = LinkExtractionService.EmbedUrlFor(d.VideoUrl)
        }).ToList();
    }

    private async Task<List<string>> DistinctTagsAsync(int teamId)
    {
        var rows = await Db.DrillTags
            .Where(t => t.Drill!.TeamId == teamId)
            .Select(t => new { t.Id, t.Name, t.NormalizedName })
            .ToListAsync();

        return rows.GroupBy(t => t.NormalizedName)
            .Select(g => g.OrderBy(t => t.Id).First().Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IActionResult> RedisplayAsync(TeamContext ctx, Drill? drill, string error,
        string? title, string? description, string? videoUrl, string? runTimeMinutes,
        string? tags, string? returnUrl)
    {
        ViewBag.NavSection = "drills";
        return View("Edit", new DrillEditViewModel
        {
            Ctx = ctx,
            Drill = drill,
            Error = error,
            AllTags = await DistinctTagsAsync(ctx.Team.Id),
            ReturnUrl = returnUrl,
            RetainedTitle = title,
            RetainedDescription = description,
            RetainedVideoUrl = videoUrl,
            RetainedRunTime = runTimeMinutes,
            RetainedTags = tags
        });
    }

    private static string? ValidateFields(string? title, string? videoUrl, string? runTimeMinutes)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Give the drill a name.";

        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            var trimmed = videoUrl.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                return "That video link doesn't look like a web address. It should start with https://";
            }
        }

        // Bound rather than just parsed. A stray "90 minutes" or a slipped keypress would
        // otherwise land in a plan total and quietly make the practice look hours long.
        if (!string.IsNullOrWhiteSpace(runTimeMinutes))
        {
            if (!int.TryParse(runTimeMinutes.Trim(), out var minutes))
                return "Run time needs to be a number of minutes, like 12.";
            if (minutes < 1 || minutes > RunTime.MaxMinutes)
                return $"Run time should be between 1 and {RunTime.MaxMinutes} minutes.";
        }

        return null;
    }

    /// <summary>
    /// Saves each uploaded picture onto the drill, in the order they were chosen. Returns a
    /// message describing what was refused, or null when everything landed.
    ///
    /// Partial success is the normal outcome to plan for: one bad file among four shouldn't cost
    /// the coach the other three, so the good ones are kept and the message names what didn't
    /// make it.
    /// </summary>
    private async Task<string?> AddDiagramsAsync(int teamId, Drill drill, List<IFormFile>? files)
    {
        var incoming = (files ?? new List<IFormFile>()).Where(f => f.Length > 0).ToList();
        if (incoming.Count == 0) return null;

        var existing = await Db.DrillDiagrams.CountAsync(d => d.DrillId == drill.Id);
        var room = MaxDiagrams - existing;
        if (room <= 0)
            return $"a drill can hold {MaxDiagrams} pictures and this one is full. " +
                   "Remove one before adding another.";

        string? firstError = null;
        var added = 0;
        var skippedForRoom = 0;

        foreach (var file in incoming)
        {
            if (added >= room) { skippedForRoom++; continue; }

            var saved = await _storage.SaveDiagramAsync(teamId, drill.Id, file);
            if (saved.Ok)
            {
                Db.DrillDiagrams.Add(new DrillDiagram
                {
                    DrillId = drill.Id, FileName = saved.FileName!, Bytes = saved.Bytes
                });
                added++;
            }
            else
            {
                firstError ??= saved.Error;
            }
        }

        if (firstError is not null)
            return added > 0
                ? $"{added} picture{(added == 1 ? "" : "s")} added and the rest weren't: {firstError}"
                : $"the picture wasn't added: {firstError}";

        if (skippedForRoom > 0)
            return $"only {added} fitted — a drill holds {MaxDiagrams} pictures.";

        return null;
    }

    /// <summary>Minutes, or null when the coach hasn't estimated it. Validated before this runs.</summary>
    private static int? ParseRunTime(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out var minutes)
            ? minutes
            : null;

    /// <summary>
    /// Brings a drill's tags in line with what was typed, touching only what changed.
    ///
    /// Diffing rather than clearing and re-adding: a coach usually edits tags by adding one to an
    /// existing set, so most saves have old and new rows sharing a NormalizedName. Deleting and
    /// re-inserting those in one SaveChanges risks the two landing in an order that trips the
    /// {DrillId, NormalizedName} unique index.
    /// </summary>
    private void SyncTags(Drill drill, string? tags)
    {
        var parsed = ParseTags(tags);
        var wanted = parsed.Select(p => p.Normalized).ToHashSet();

        Db.DrillTags.RemoveRange(drill.Tags.Where(t => !wanted.Contains(t.NormalizedName)));

        var existing = drill.Tags.Select(t => t.NormalizedName).ToHashSet();
        foreach (var (name, norm) in parsed)
            if (!existing.Contains(norm))
                Db.DrillTags.Add(new DrillTag { DrillId = drill.Id, Name = name, NormalizedName = norm });
    }

    /// <summary>Pictures one drill can hold. Enough for a multi-stage progression, low enough
    /// that a library can't quietly eat the volume. Public so the form can state the limit
    /// rather than hard-coding a number that could drift away from the check.</summary>
    public const int MaxDiagrams = 6;

    private const int MaxTagsPerDrill = 15;

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
            if (result.Count >= MaxTagsPerDrill) break;
        }

        return result;
    }
}
