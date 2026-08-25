using HockeyPractice.Persistence;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.Util;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

[Route("t/{slug}")]
public class TeamController : TeamScopedController
{
    private readonly DataPaths _paths;

    public TeamController(AppDbContext db, TeamAccessService access, DataPaths paths)
        : base(db, access) => _paths = paths;

    /// <summary>
    /// Entry point. A join link carries the code as ?c= so a player taps once and never types.
    /// The code is swapped for the cookie and stripped from the URL immediately — it should not
    /// sit in browser history or survive a screenshot of the address bar.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, string? c)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(c))
        {
            var granted = await TryGrantAsync(team, c);
            if (granted != TeamAccessLevel.None)
                return RedirectToAction(nameof(Plans), new { slug });
        }

        if (Access.LevelFor(User, team.Id) == TeamAccessLevel.None)
            return RedirectToAction(nameof(EnterCode), new { slug });

        return RedirectToAction(nameof(Plans), new { slug });
    }

    [HttpGet("code")]
    public async Task<IActionResult> EnterCode(string slug, string? returnUrl)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        return View(new EnterCodeViewModel
        {
            Team = team,
            ReturnUrl = returnUrl,
            LogoUrl = LogoUrlFor(team)
        });
    }

    [HttpPost("code")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("code-entry")]
    public async Task<IActionResult> EnterCode(string slug, string accessCode, string? returnUrl)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        var granted = await TryGrantAsync(team, accessCode);
        if (granted == TeamAccessLevel.None)
        {
            return View(new EnterCodeViewModel
            {
                Team = team,
                ReturnUrl = returnUrl,
                LogoUrl = LogoUrlFor(team),
                Error = "That code didn't work. Check with your coach."
            });
        }

        if (TryReturnUrlRedirect(returnUrl, out var back))
            return back;

        // Players land on the roster picker once; coaches go straight to work.
        if (granted == TeamAccessLevel.Viewer && Access.PlayerFor(User, team.Id) is null)
            return RedirectToAction(nameof(WhoAmI), new { slug });

        return RedirectToAction(nameof(Plans), new { slug });
    }

    /// <summary>One tap to say which player you are. Skippable, and changeable later.</summary>
    [HttpGet("whoami")]
    public async Task<IActionResult> WhoAmI(string slug, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Viewer);
        if (failure is not null) return failure;

        var players = await Db.Players
            .Where(p => p.TeamId == ctx!.Team.Id && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View(new RosterPickViewModel { Ctx = ctx!, Players = players, ReturnUrl = returnUrl });
    }

    [HttpPost("whoami")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WhoAmI(string slug, int? playerId, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Viewer);
        if (failure is not null) return failure;

        // Only accept a player that actually belongs to this team.
        if (playerId is int id &&
            !await Db.Players.AnyAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id))
        {
            playerId = null;
        }

        await Access.SetPlayerAsync(HttpContext, ctx!.Team.Id, playerId);

        return TryReturnUrlRedirect(returnUrl, out var back)
            ? back
            : RedirectToAction(nameof(Plans), new { slug });
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(string slug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Viewer);
        if (failure is not null) return failure;

        var team = ctx!.Team;
        var viewerKey = Access.ViewerKeyFor(User);

        var query = Db.Plans.Where(p => p.TeamId == team.Id);
        // Drafts are the coach's alone — a player must not see a plan that isn't finished.
        if (!ctx.IsCoach) query = query.Where(p => p.Status == PlanStatus.Published);

        var plans = await query
            .Select(p => new
            {
                Plan = p,
                Videos = p.Links.Count(l => !l.IsHidden)
            })
            .OrderByDescending(x => x.Plan.PracticeDateLocal)
            .ToListAsync();

        // Which of these the viewer has already opened. Resolved as its own query so the
        // player-vs-device branch stays out of the expression tree EF has to translate.
        var mine = await MyViewedPlanIdsAsync(team.Id, ctx.Me?.Id, viewerKey);

        var cards = plans.Select(x => new PlanCard
        {
            Plan = x.Plan,
            VideoCount = x.Videos,
            ViewedByMe = mine.Contains(x.Plan.Id),
            WhenLabel = WhenLabel.For(x.Plan.PracticeDateLocal, team.TimeZoneId)
        }).ToList();

        // A practice stays "current" until a couple of hours after it starts, so the page
        // doesn't shove tonight's plan into the archive while the team is still on the ice.
        var cutoff = WhenLabel.NowIn(team.TimeZoneId).AddHours(-2);
        var upcoming = cards.Where(c => c.Plan.PracticeDateLocal >= cutoff)
                            .OrderBy(c => c.Plan.PracticeDateLocal).ToList();
        var past = cards.Where(c => c.Plan.PracticeDateLocal < cutoff).ToList();

        ViewBag.NavSection = "plans";
        return View(new PlanListViewModel
        {
            Ctx = ctx,
            Next = upcoming.FirstOrDefault(),
            Upcoming = upcoming.Skip(1).ToList(),
            Past = past
        });
    }

    /// <summary>Team logo. Served through the app because it lives on the volume, not in wwwroot.</summary>
    [HttpGet("logo")]
    public async Task<IActionResult> Logo(string slug)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team?.LogoFileName is null) return NotFound();

        var path = Path.Combine(_paths.TeamDirectory(team.Id), team.LogoFileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        return PhysicalFile(path, contentType);
    }

    /// <summary>
    /// Flips a coach into the player's view and back. Purely presentational — it can only
    /// clamp what is shown down to Viewer, never grant anything, so there is nothing to
    /// escalate here.
    /// </summary>
    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePreview(string slug, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Coach);
        if (failure is not null) return failure;

        var team = ctx!.Team;
        var on = !Access.IsPreviewingAsPlayer(Request, team.Id);
        Access.SetPreview(Response, Request, team.Id, on);

        // Coming back from player view lands on Manage; going in lands on the plan list —
        // both are what you actually wanted next. A returnUrl on a page the other role can't
        // see would just bounce.
        if (!on)
            return RedirectToAction("Index", "Coach", new { slug });

        if (TryReturnUrlRedirect(returnUrl, out var back)) return back;
        return RedirectToAction(nameof(Plans), new { slug });
    }

    [HttpPost("signout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutOfTeam(string slug)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        await Access.GrantTeamAsync(HttpContext, team.Id, TeamAccessLevel.None);
        await Access.SetPlayerAsync(HttpContext, team.Id, null);
        return RedirectToAction(nameof(EnterCode), new { slug });
    }

    /// <summary>
    /// Plans this viewer has already opened. Matches on the roster player when one has been
    /// picked so a player is recognised across their phone and the family iPad; otherwise
    /// falls back to the per-device key.
    /// </summary>
    private async Task<HashSet<int>> MyViewedPlanIdsAsync(int teamId, int? playerId, string viewerKey)
    {
        if (playerId is null && string.IsNullOrEmpty(viewerKey))
            return new HashSet<int>();

        var views = Db.PlanViews.Where(v => v.PracticePlan!.TeamId == teamId);

        views = playerId is int id
            ? views.Where(v => v.PlayerId == id)
            : views.Where(v => v.ViewerKey == viewerKey);

        return (await views.Select(v => v.PracticePlanId).ToListAsync()).ToHashSet();
    }

    /// <summary>Checks the submitted code against both tiers and grants the higher one it matches.</summary>
    private async Task<TeamAccessLevel> TryGrantAsync(Team team, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return TeamAccessLevel.None;

        var level = TeamAccessLevel.None;
        if (Security.CodeMatches(code, team.CoachCodeHash)) level = TeamAccessLevel.Coach;
        else if (Security.CodeMatches(code, team.ViewCodeHash)) level = TeamAccessLevel.Viewer;

        if (level != TeamAccessLevel.None)
            await Access.GrantTeamAsync(HttpContext, team.Id, level);

        return level;
    }
}
