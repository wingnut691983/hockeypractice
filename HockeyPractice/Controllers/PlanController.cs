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

        var plan = await Db.Plans.FirstOrDefaultAsync(
            p => p.Id == id && p.TeamId == ctx!.Team.Id && p.Status == PlanStatus.Published);
        if (plan is null) return NotFound();

        var viewerKey = Access.ViewerKeyFor(User);
        if (string.IsNullOrEmpty(viewerKey)) return NoContent();

        var existing = await Db.PlanViews
            .FirstOrDefaultAsync(v => v.PracticePlanId == plan.Id && v.ViewerKey == viewerKey);

        if (existing is null)
        {
            Db.PlanViews.Add(new PlanView
            {
                PracticePlanId = plan.Id,
                PlayerId = ctx!.Me?.Id,
                ViewerKey = viewerKey
            });
        }
        else if (existing.PlayerId is null && ctx!.Me is not null)
        {
            // The device viewed anonymously first and has since picked a roster name —
            // attach it rather than creating a second row for the same person.
            existing.PlayerId = ctx.Me.Id;
        }
        else
        {
            return NoContent();
        }

        try
        {
            await Db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two tabs racing the same beacon; the unique index did its job.
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
