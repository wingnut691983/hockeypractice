using HockeyPractice.Persistence;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

/// <summary>
/// Shared team lookup and access gate. Every team-scoped page resolves through here so the
/// code check can't be forgotten when a new action is added.
/// </summary>
public abstract class TeamScopedController : Controller
{
    protected readonly AppDbContext Db;
    protected readonly TeamAccessService Access;

    protected TeamScopedController(AppDbContext db, TeamAccessService access)
    {
        Db = db;
        Access = access;
    }

    /// <summary>
    /// Resolves the team and the viewer's access to it. When <c>Failure</c> is non-null the
    /// caller must return it unchanged — the viewer is being sent to the code page or refused.
    /// </summary>
    protected async Task<(TeamContext? Ctx, IActionResult? Failure)> ResolveAsync(
        string slug, TeamAccessLevel minimum)
    {
        var team = await Db.Teams.FirstOrDefaultAsync(t => t.Slug == slug);
        if (team is null)
            return (null, NotFound());

        var realLevel = Access.RealLevelFor(User, team.Id);
        // Authorisation always uses the real level; preview only changes what is rendered.
        var level = realLevel;

        if (level < minimum)
        {
            // No access at all → ask for the code.
            // Some access but not enough (a player following a coach link) → send them back to
            // the plan list. Forbid() would bounce to the cookie scheme's AccessDeniedPath,
            // which this app doesn't define, and the code page would just loop them.
            IActionResult failure = level == TeamAccessLevel.None
                ? RedirectToAction("EnterCode", "Team",
                    new { slug, returnUrl = CurrentPathForReturn() })
                : RedirectToAction("Plans", "Team", new { slug });
            return (null, failure);
        }

        Player? me = null;
        if (Access.PlayerFor(User, team.Id) is int playerId)
            me = await Db.Players.FirstOrDefaultAsync(p => p.Id == playerId && p.TeamId == team.Id);

        return (new TeamContext
        {
            Team = team,
            Level = Access.DisplayLevelFor(User, team.Id, Request),
            RealLevel = realLevel,
            Me = me,
            LogoUrl = LogoUrlFor(team),
            ViaSiteAdmin = Access.IsViaSiteAdmin(User, team.Id),
            IsParent = Access.IsParent(User, team.Id),
            OtherTeams = await OtherTeamsAsync(team.Id)
        }, null);
    }

    /// <summary>
    /// Teams this device already has access to, for the header switcher. A site admin sees
    /// every team; anyone else sees only the ones they hold a claim for, so the switcher can
    /// never advertise a team they can't open.
    /// </summary>
    private async Task<List<TeamLink>> OtherTeamsAsync(int currentTeamId)
    {
        // Only teams this device actually holds a claim for. A site admin gets no special
        // listing here — administering the site is not access to a team's plans, and a
        // switcher offering teams you'd then be asked to enter a code for is just a dead end.
        var reachable = User.Claims
            .Where(c => c.Type.StartsWith(TeamAccessService.TeamClaimPrefix, StringComparison.Ordinal))
            .Select(c => int.TryParse(c.Type[TeamAccessService.TeamClaimPrefix.Length..], out var id) ? id : -1)
            .Where(id => id > 0 && id != currentTeamId)
            .ToList();

        if (reachable.Count == 0) return new List<TeamLink>();

        var query = Db.Teams.Where(t => t.Id != currentTeamId && reachable.Contains(t.Id));

        return await query
            .OrderBy(t => t.Name)
            .Select(t => new TeamLink(t.Slug, t.Name))
            .ToListAsync();
    }

    protected string? LogoUrlFor(Team team) =>
        team.LogoFileName is null ? null : Url.Action("Logo", "Team", new { slug = team.Slug });

    /// <summary>
    /// The current URL, including the reverse proxy's path prefix, for round-tripping through
    /// a sign-in redirect. `Request.Path` alone excludes PathBase — UsePathBase strips it — so
    /// capturing that produces a URL that lands outside the app when redirected to.
    /// </summary>
    protected string CurrentPathForReturn() =>
        $"{Request.PathBase}{Request.Path}{Request.QueryString}";

    /// <summary>
    /// Redirects to a captured returnUrl, restoring the path prefix when it is missing and
    /// refusing anything that isn't a local path. Returns false when there is nothing safe to
    /// redirect to, so the caller can fall back to its own destination.
    /// </summary>
    protected bool TryReturnUrlRedirect(string? returnUrl, out IActionResult redirect)
    {
        redirect = null!;
        if (string.IsNullOrWhiteSpace(returnUrl)) return false;

        var target = returnUrl!;

        // Only a plain rooted path is acceptable. Rejecting "//host" and any absolute URL here,
        // before the repair below, matters: prepending the prefix to "//evil.com" would turn a
        // clearly-bogus value into something that then looks local enough to pass.
        if (!target.StartsWith('/') || target.StartsWith("//") || target.Contains(':'))
            return false;

        var basePath = Request.PathBase.Value ?? string.Empty;

        // Repair a prefix-less path rather than emitting a URL the gateway can't route.
        // Covers any capture site and links issued before this was fixed, not just the one above.
        if (basePath.Length > 0 &&
            !target.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, basePath, StringComparison.OrdinalIgnoreCase))
        {
            target = basePath + target;
        }

        // Belt and braces: the framework's own local-URL check on the final value.
        if (!Url.IsLocalUrl(target)) return false;

        redirect = Redirect(target);
        return true;
    }
}
