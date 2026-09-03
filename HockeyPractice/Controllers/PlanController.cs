using HockeyPractice.Persistence;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

[Route("t/{slug}/plans")]
public class PlanController : TeamScopedController
{
    private readonly PlanStorageService _storage;

    public PlanController(AppDbContext db, TeamAccessService access, PlanStorageService storage)
        : base(db, access) => _storage = storage;

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var plan = await Db.Plans
            .Include(p => p.Links)
            .FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);

        if (plan is null) return NotFound();
        if (plan.Status != PlanStatus.Published && !ctx!.IsManager) return NotFound();

        // A drill plan's content lives here, not in a file. The coach's editor is built by a
        // different action, so leaving this out would show them a complete plan and the team an
        // empty one — with nothing to hint at the difference.
        var drills = new List<DrillCard>();
        if (plan.Kind == PlanKind.Drills)
        {
            var entries = await Db.PlanDrills
                .Include(pd => pd.Drill).ThenInclude(d => d!.Diagrams)
                .Where(pd => pd.PracticePlanId == plan.Id)
                .OrderBy(pd => pd.SortOrder).ThenBy(pd => pd.Id)
                .ToListAsync();

            // Deliberately no .ThenInclude(d => d.Tags): tags are the coach's filing system and
            // are never shown to players, so loading them here would be wasted work on the
            // busiest page in the app.
            drills = entries.Select(pd => new DrillCard
            {
                Drill = pd.Drill!,
                PlanDrillId = pd.Id,
                EmbedUrl = LinkExtractionService.EmbedUrlFor(pd.Drill!.VideoUrl)
            }).ToList();
        }

        // Roster tracking is the coach's view only. A player or parent sees their own badge
        // and nothing about anybody else on the team.
        var viewed = new List<Player>();
        var notViewed = new List<Player>();
        var anonymous = 0;

        if (ctx!.IsManager)
        {
            var roster = await Db.Players
                .Where(p => p.TeamId == ctx.Team.Id && p.IsActive)
                .OrderBy(p => p.Name)
                .ToListAsync();

            var viewedIds = (await Db.PlanViews
                .Where(v => v.PracticePlanId == plan.Id && v.PlayerId != null)
                .Select(v => v.PlayerId!.Value)
                .Distinct()
                .ToListAsync()).ToHashSet();

            viewed = roster.Where(p => viewedIds.Contains(p.Id)).ToList();
            notViewed = roster.Where(p => !viewedIds.Contains(p.Id)).ToList();
            anonymous = await Db.PlanViews
                .CountAsync(v => v.PracticePlanId == plan.Id && v.PlayerId == null);
        }

        return View(new PlanDetailViewModel
        {
            Ctx = ctx,
            Plan = plan,
            Videos = plan.Links.Where(l => !l.IsHidden).OrderBy(l => l.SortOrder).ToList(),
            Drills = drills,
            WhenLabel = WhenLabel.For(plan.PracticeDateLocal, ctx.Team.TimeZoneId),
            ViewedByMe = await HasViewedAsync(plan.Id, ctx.Me?.Id, Access.ViewerKeyFor(User)),
            Viewed = viewed,
            NotViewed = notViewed,
            AnonymousViews = anonymous
        });
    }

    /// <summary>
    /// Streams the PDF. Uploads live outside wwwroot precisely so this action runs first —
    /// static hosting would let anyone who guesses a path read the team's plans.
    /// </summary>
    [HttpGet("{id:int}/file")]
    public async Task<IActionResult> File(string slug, int id, bool download = false)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var plan = await Db.Plans.FirstOrDefaultAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id);
        if (plan is null) return NotFound();
        if (plan.Status != PlanStatus.Published && !ctx!.IsManager) return NotFound();
        if (!_storage.Exists(ctx!.Team.Id, plan.Id)) return NotFound();

        var stream = _storage.Open(ctx.Team.Id, plan.Id);

        // Inline by default so pdf.js renders it in place. Passing a filename is what makes
        // ASP.NET set Content-Disposition: attachment, so only do it for an explicit download.
        return download
            ? base.File(stream, "application/pdf", plan.OriginalFileName)
            : base.File(stream, "application/pdf");
    }

    /// <summary>
    /// Recorded once the viewer has actually rendered the document, not on page load —
    /// a bounce is not a view, and "12 of 17 viewed" is only useful if it means something.
    /// </summary>
    [HttpPost("{id:int}/viewed")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Viewed(string slug, int id)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        // A manager opening their own plan to check who has read it is not a "view" — without
        // this, every glance at the tracking dashboard silently inflated the anonymous-view
        // count, since the same client-side beacon fires on this page for everyone.
        if (ctx!.IsManager) return NoContent();

        var plan = await Db.Plans.FirstOrDefaultAsync(
            p => p.Id == id && p.TeamId == ctx.Team.Id && p.Status == PlanStatus.Published);
        if (plan is null) return NotFound();

        var viewerKey = Access.ViewerKeyFor(User);
        if (string.IsNullOrEmpty(viewerKey)) return NoContent();

        // Identity, not device, is what must be unique. A family device is legitimately shared
        // by more than one player over a season — keying purely on ViewerKey meant a second
        // player picking themselves on a phone their sibling already used could never be
        // recorded; the existing row was permanently claimed by whoever viewed it first. Once a
        // player is known, THAT is the identity to look up and dedupe on; the device only stands
        // in when nobody has been identified yet (a parent who skipped the roster pick).
        var existing = ctx.Me is not null
            ? await Db.PlanViews.FirstOrDefaultAsync(v => v.PracticePlanId == plan.Id && v.PlayerId == ctx.Me.Id)
            : await Db.PlanViews.FirstOrDefaultAsync(v => v.PracticePlanId == plan.Id && v.PlayerId == null && v.ViewerKey == viewerKey);

        if (existing is null)
        {
            Db.PlanViews.Add(new PlanView
            {
                PracticePlanId = plan.Id,
                PlayerId = ctx.Me?.Id,
                ViewerKey = viewerKey
            });
        }
        else
        {
            // Already recorded for this identity. Refresh which device it was last seen on —
            // harmless, and keeps the row pointing at the device that actually viewed it most
            // recently rather than whichever device happened to view it first.
            existing.ViewerKey = viewerKey;
        }

        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two tabs racing the same beacon for the same identity; the unique index did its job.
        }

        return NoContent();
    }

    private async Task<bool> HasViewedAsync(int planId, int? playerId, string viewerKey)
    {
        if (playerId is int id)
            return await Db.PlanViews.AnyAsync(v => v.PracticePlanId == planId && v.PlayerId == id);

        return !string.IsNullOrEmpty(viewerKey) &&
               await Db.PlanViews.AnyAsync(v => v.PracticePlanId == planId && v.ViewerKey == viewerKey);
    }
}
