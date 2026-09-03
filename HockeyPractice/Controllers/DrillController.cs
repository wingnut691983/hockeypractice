using HockeyPractice.Persistence;
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
        string? videoUrl, string? tags, IFormFile? diagram, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var error = ValidateFields(title, videoUrl);
        if (error is not null)
            return await RedisplayAsync(ctx!, null, error, title, description, videoUrl, tags, returnUrl);

        var drill = new Drill
        {
            TeamId = ctx!.Team.Id,
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            VideoUrl = string.IsNullOrWhiteSpace(videoUrl) ? null : videoUrl.Trim()
        };

        // Saved first so the drill has an id to key its directory off — same two-phase write the
        // plan upload uses.
        Db.Drills.Add(drill);
        await Db.SaveChangesAsync();

        foreach (var (name, norm) in ParseTags(tags))
            Db.DrillTags.Add(new DrillTag { DrillId = drill.Id, Name = name, NormalizedName = norm });

        var notice = "Drill saved.";
        if (diagram is { Length: > 0 })
        {
            var saved = await _storage.SaveDiagramAsync(ctx.Team.Id, drill.Id, diagram);
            if (saved.Ok)
            {
                drill.DiagramFileName = saved.FileName;
                drill.DiagramBytes = saved.Bytes;
            }
            else
            {
                // Keep the drill rather than discarding everything the coach typed over a bad
                // file — unlike a plan, a drill is perfectly useful without a diagram.
                notice = $"Drill saved, but the diagram wasn't: {saved.Error}";
            }
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

        var drill = await Db.Drills.Include(d => d.Tags)
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
        string? videoUrl, string? tags, IFormFile? diagram)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
        if (failure is not null) return failure;

        var drill = await Db.Drills.Include(d => d.Tags)
            .FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill is null) return NotFound();

        var error = ValidateFields(title, videoUrl);
        if (error is not null)
            return await RedisplayAsync(ctx!, drill, error, title, description, videoUrl, tags, null);

        drill.Title = title.Trim();
        drill.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        drill.VideoUrl = string.IsNullOrWhiteSpace(videoUrl) ? null : videoUrl.Trim();

        SyncTags(drill, tags);

        var notice = $"Saved \"{drill.Title}\".";
        var diagramFailed = false;
        if (diagram is { Length: > 0 })
        {
            var saved = await _storage.SaveDiagramAsync(ctx!.Team.Id, drill.Id, diagram);
            if (saved.Ok)
            {
                var previous = drill.DiagramFileName;
                drill.DiagramFileName = saved.FileName;
                drill.DiagramBytes = saved.Bytes;

                // Remove the file it replaced, so a swapped diagram doesn't quietly hold quota.
                if (previous is not null && previous != saved.FileName)
                    _storage.DeleteDiagram(ctx.Team.Id, drill.Id, previous);
            }
            else
            {
                diagramFailed = true;
                notice = $"Saved, but the new diagram wasn't accepted: {saved.Error}";
            }
        }

        await Db.SaveChangesAsync();

        // Saving is the end of editing, so go back to the library — the same place Archive and
        // Delete already land, and the drill is right there to confirm the change took.
        // The exception is a rejected diagram: bouncing away would leave the coach re-finding the
        // drill to try another picture, so that one stays put with the reason on screen.
        return diagramFailed
            ? RedirectToAction(nameof(Edit), new { slug, id, notice })
            : RedirectToAction(nameof(Index), new { slug, notice });
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

        var drill = await Db.Drills.Include(d => d.Tags)
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

        var source = await Db.Drills.Include(d => d.Tags)
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
    [HttpGet("{id:int}/diagram")]
    public async Task<IActionResult> Diagram(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var drill = await Db.Drills.FirstOrDefaultAsync(d => d.Id == id && d.TeamId == ctx!.Team.Id);
        if (drill?.DiagramFileName is null) return NotFound();
        if (!_storage.DiagramExists(ctx!.Team.Id, drill.Id, drill.DiagramFileName)) return NotFound();

        var path = _storage.DiagramPath(ctx.Team.Id, drill.Id, drill.DiagramFileName);
        var contentType = drill.DiagramFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "image/webp";

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
            CopiedFromDrillId = drill.Id
        };

        Db.Drills.Add(copy);
        await Db.SaveChangesAsync();   // need the new id before writing its directory

        foreach (var tag in drill.Tags)
            Db.DrillTags.Add(new DrillTag
            {
                DrillId = copy.Id, Name = tag.Name, NormalizedName = tag.NormalizedName
            });

        if (drill.DiagramFileName is { } fileName)
        {
            var copied = _storage.CopyDiagram(fromTeamId, drill.Id, toTeamId, copy.Id, fileName);
            if (copied is not null)
            {
                copy.DiagramFileName = copied;
                copy.DiagramBytes = drill.DiagramBytes;
            }
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

        var query = Db.Drills.Include(d => d.Tags)
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
        string? title, string? description, string? videoUrl, string? tags, string? returnUrl)
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
            RetainedTags = tags
        });
    }

    private static string? ValidateFields(string? title, string? videoUrl)
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

        return null;
    }

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
