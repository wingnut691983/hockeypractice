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
        : base(db, access)
    {
        _paths = paths;
    }

    /// <summary>
    /// Entry point. A join link carries the code as ?c= so a player taps once and never types.
    /// The code is swapped for the cookie and stripped from the URL immediately — it should not
    /// sit in browser history or survive a screenshot of the address bar.
    /// </summary>
    [HttpGet("")]
    [EnableRateLimiting("code-entry")]   // ?c= checks a code, so it gets the same guard as the form
    public async Task<IActionResult> Index(string slug, string? c)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null)
        {
            // A 404 renders through UseStatusCodePagesWithReExecute, which leaves the browser URL
            // untouched, so returning one here stranded the code in the address bar of a link
            // whose slug was mistyped or whose team was deleted. Redirect to the same path without
            // it and 404 on the clean URL. One extra hop, only on a path that was already an error.
            if (!string.IsNullOrWhiteSpace(c)) return RedirectToAction(nameof(Index), new { slug });
            return NotFound();
        }

        if (!string.IsNullOrWhiteSpace(c))
        {
            var granted = await TryGrantAsync(team, c);

            // Same flow as the code form: a new player picks their name before anything else.
            // The join link is the MAIN way players arrive (the coach shares it in the group
            // chat), so skipping the question here undercounted almost everyone.
            if (granted == TeamAccessLevel.Player &&
                Access.PlayerFor(User, team.Id) is null &&
                !Access.HasDeclaredIdentity(User, team.Id) &&
                await Db.Players.AnyAsync(p => p.TeamId == team.Id && p.IsActive))
            {
                return RedirectToAction(nameof(WhoAmI), new { slug });
            }

            if (granted != TeamAccessLevel.None)
                return RedirectToAction(nameof(Plans), new { slug });
        }

        if (Access.LevelFor(User, team.Id) == TeamAccessLevel.None)
            return RedirectToAction(nameof(EnterCode), new { slug });

        return RedirectToAction(nameof(Plans), new { slug });
    }

    [HttpGet("code")]
    public async Task<IActionResult> EnterCode(string slug, string? returnUrl, bool manage = false)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null) return NotFound();

        // Already a manager and asking for the manage screen — don't make them re-enter a code.
        if (manage && Access.RealLevelFor(User, team.Id) >= TeamAccessLevel.Manager)
            return RedirectToAction("Index", "Coach", new { slug });

        var preview = await SharedPlanPreviewAsync(team, returnUrl);

        return View(new EnterCodeViewModel
        {
            Team = team,
            ReturnUrl = returnUrl,
            LogoUrl = LogoUrlFor(team),
            ManageMode = manage,
            PreviewPlanTitle = preview.Title,
            PreviewPlanUrl = preview.Url
        });
    }

    /// <summary>
    /// The title of the plan a visitor was heading for when the gate stopped them, for the link
    /// preview and nothing else. Null unless the returnUrl names one published plan of this team.
    ///
    /// Published only, and that is the point rather than a detail: a draft is a plan the coach has
    /// not shown anyone yet, and its title is often exactly what they are still deciding. Scoped
    /// on TeamId as well as the parsed slug, so neither half can be worked around on its own.
    /// </summary>
    private async Task<(string? Title, string? Url)> SharedPlanPreviewAsync(
        Team team, string? returnUrl)
    {
        var planId = PlanPreview.PlanIdFromReturnUrl(returnUrl, Request.PathBase.Value, team.Slug);
        if (planId is null) return (null, null);

        var title = await Db.Plans
            .Where(p => p.Id == planId && p.TeamId == team.Id && p.Status == PlanStatus.Published)
            .Select(p => p.Title)
            .FirstOrDefaultAsync();

        if (title is null) return (null, null);

        // Rebuilt from the id, never echoed from returnUrl. See EnterCodeViewModel.PreviewPlanUrl.
        return (title, Url.Action("Details", "Plan", new { slug = team.Slug, id = planId }));
    }

    [HttpPost("code")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("code-entry")]
    public async Task<IActionResult> EnterCode(string slug, string accessCode, string? returnUrl,
        bool manage = false)
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
                ManageMode = manage,
                Error = manage
                    ? "That manager code didn't work."
                    : "That code didn't work. Check with your coach."
            });
        }

        // Asked for the manage screen but entered the team code — say so plainly instead of
        // silently dropping them somewhere they didn't ask to be.
        if (manage && granted < TeamAccessLevel.Manager)
        {
            return View(new EnterCodeViewModel
            {
                Team = team,
                ReturnUrl = returnUrl,
                LogoUrl = LogoUrlFor(team),
                ManageMode = true,
                Error = "That's the team code, which gets you the practice plans. " +
                        "Managing the team needs the separate manager code."
            });
        }

        if (granted >= TeamAccessLevel.Manager)
        {
            if (TryReturnUrlRedirect(returnUrl, out var managerBack)) return managerBack;

            // Managers land on the plans, same as everyone else. Seeing what the team sees is
            // the more common reason to open the site; adding a plan is one tap from there.
            return RedirectToAction(nameof(Plans), new { slug });
        }

        // Players pick their name once, so the coach can see who has read a plan. This is
        // checked BEFORE returnUrl: arriving via a deep link (the landing page's "See practice
        // plans" button sets one) otherwise skipped the question entirely, and the player was
        // never counted as having read anything. Where they were headed is carried through.
        if (!Access.HasDeclaredIdentity(User, team.Id) &&
            await Db.Players.AnyAsync(p => p.TeamId == team.Id && p.IsActive))
        {
            return RedirectToAction(nameof(WhoAmI), new { slug, returnUrl });
        }

        if (TryReturnUrlRedirect(returnUrl, out var back))
            return back;

        return RedirectToAction(nameof(Plans), new { slug });
    }

    /// <summary>One tap to say which player you are. Skippable, and changeable later.</summary>
    [HttpGet("whoami")]
    public async Task<IActionResult> WhoAmI(string slug, string? returnUrl)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var players = await Db.Players
            .Where(p => p.TeamId == ctx!.Team.Id && p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        return View(new RosterPickViewModel { Ctx = ctx!, Players = players, ReturnUrl = returnUrl });
    }

    [HttpPost("whoami")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WhoAmI(string slug, int? playerId, string? returnUrl,
        bool parent = false)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        // Only accept a player that actually belongs to this team.
        if (playerId is int id &&
            !await Db.Players.AnyAsync(p => p.Id == id && p.TeamId == ctx!.Team.Id))
        {
            playerId = null;
        }

        await Access.SetPlayerAsync(HttpContext, ctx!.Team.Id, playerId, parent);

        return TryReturnUrlRedirect(returnUrl, out var back)
            ? back
            : RedirectToAction(nameof(Plans), new { slug });
    }

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(string slug)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var team = ctx!.Team;
        var viewerKey = Access.ViewerKeyFor(User);

        // Read-only list page. The projection below still materialises Plan entities, so the
        // snapshots are real cost and nothing here writes.
        var query = Db.Plans.AsNoTracking().Where(p => p.TeamId == team.Id);
        // Drafts are the coach's alone — a player must not see a plan that isn't finished.
        if (!ctx.IsManager) query = query.Where(p => p.Status == PlanStatus.Published);

        var plans = await query
            .Select(p => new
            {
                Plan = p,
                Videos = p.Links.Count(l => !l.IsHidden),
                Drills = p.Drills.Count
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
            DrillCount = x.Drills,
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

        // Every link the app writes carries ?v=<the file name's GUID>, which changes on every
        // upload, so a request that names the current logo can be cached for a year. Without the
        // token the bytes here may already have been replaced, so it revalidates instead; that is
        // cheap, because PhysicalFile sets Last-Modified and answers 304.
        //
        // "public", unlike the diagram and overview pictures, which are "private". This action
        // takes no access check on purpose: it is the og:image in _Layout, and a chat app
        // unfurling a team link fetches it with no team cookie. Marking it private would stop
        // those previews being cached at all. See Team.LogoVersion.
        // The null check is first for the same reason as the other two: an absent ?v= reads as
        // StringValues.Empty, which compares equal to a null string.
        Response.Headers.CacheControl =
            team.LogoVersion is not null && Request.Query["v"] == team.LogoVersion
                ? "public, max-age=31536000, immutable"
                : "public, no-cache";

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
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Manager);
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

        // Out to the team list, not back to this team's code box. Signing out and being handed
        // the very gate you just left reads as a failed sign-out, and left no way to reach any
        // other team without editing the URL.
        return RedirectToAction("Index", "Home");
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

        if (Security.ManagerCodeMatches(code, team.CoachCodeHash))
        {
            level = TeamAccessLevel.Manager;

            // Upgrade a row still in the old format, now that we have the plaintext in hand and
            // know it is right. This is here for rows a RESTORE brings back from an archive older
            // than the upgrade, so they heal the first time they are used. It is deliberately not
            // the way the existing rows get upgraded: this is the only read of CoachCodeHash in
            // the app, the access cookie lasts 180 days and slides, so a coach may simply never
            // type their code again. Rotating the codes is what does that.
            if (Security.IsLegacyManagerHash(team.CoachCodeHash))
            {
                team.CoachCodeHash = Security.HashManagerCode(code);
                await Db.SaveChangesAsync();
            }
        }
        else if (Security.CodeMatches(code, team.ViewCodeHash))
        {
            level = TeamAccessLevel.Player;
        }

        if (level != TeamAccessLevel.None)
            await Access.GrantTeamAsync(HttpContext, team.Id, level);

        return level;
    }
}
