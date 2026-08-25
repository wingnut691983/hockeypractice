using HockeyPractice.Persistence;
using HockeyPractice.Models;
using HockeyPractice.Services;
using HockeyPractice.Util;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace HockeyPractice.Controllers;

/// <summary>
/// Site owner surface: create teams and hand out their first codes.
/// Gated by SITE_ADMIN_CODE, which fails closed — if the variable is unset nothing is accepted,
/// rather than falling back to a default that would be a published backdoor.
/// </summary>
[Route("admin")]
public class SiteAdminController : Controller
{
    private readonly AppDbContext _db;
    private readonly TeamAccessService _access;
    private readonly PlanStorageService _storage;
    private readonly ILogger<SiteAdminController> _log;
    private readonly string? _adminCodeHash;

    public SiteAdminController(AppDbContext db, TeamAccessService access,
        PlanStorageService storage, IConfiguration config, ILogger<SiteAdminController> log)
    {
        _db = db;
        _access = access;
        _storage = storage;
        _log = log;

        var code = config["SITE_ADMIN_CODE"];
        _adminCodeHash = string.IsNullOrWhiteSpace(code) ? null : Security.HashCode(code);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? notice)
    {
        if (!_access.IsSiteAdmin(User))
            return View("AdminLogin", new AdminViewModel { Configured = _adminCodeHash is not null });

        return View(await BuildAsync(notice));
    }

    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("code-entry")]
    public async Task<IActionResult> Login(string adminCode)
    {
        if (_adminCodeHash is null || !Security.CodeMatches(adminCode, _adminCodeHash))
        {
            return View("AdminLogin", new AdminViewModel
            {
                Configured = _adminCodeHash is not null,
                Error = _adminCodeHash is null
                    ? "Site admin is not configured. Set SITE_ADMIN_CODE and restart."
                    : "That code didn't work."
            });
        }

        await _access.GrantSiteAdminAsync(HttpContext);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Drops the site-admin claim without touching any team access on this device.</summary>
    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _access.RevokeSiteAdminAsync(HttpContext);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost("teams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTeam(string name, string? slug, string? timeZoneId)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        if (string.IsNullOrWhiteSpace(name))
            return View("Index", await BuildAsync(null, "Give the team a name."));

        var candidate = Slugify(string.IsNullOrWhiteSpace(slug) ? name : slug);
        if (candidate.Length == 0)
            return View("Index", await BuildAsync(null, "That name doesn't produce a usable URL. Set the slug manually."));

        if (await _db.Teams.AnyAsync(t => t.Slug == candidate))
            return View("Index", await BuildAsync(null, $"The slug \"{candidate}\" is already taken."));

        // Both codes are shown once, here. Only their hashes are stored.
        var viewCode = Security.NewAccessCode();
        var coachCode = Security.NewAccessCode(8);

        _db.Teams.Add(new Team
        {
            Name = name.Trim(),
            Slug = candidate,
            ViewCodeHash = Security.HashCode(viewCode),
            CoachCodeHash = Security.HashCode(coachCode),
            TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "America/Chicago" : timeZoneId.Trim()
        });
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new
        {
            notice = $"Created {name.Trim()} — team code {viewCode}, coach code {coachCode}. " +
                     "Write these down now; they are not stored and cannot be shown again."
        });
    }

    /// <summary>
    /// Break-glass: takes manager access to a team without knowing its code.
    ///
    /// Deliberately an action rather than an ambient right. A site admin is not a manager of
    /// every team by default — that silent elevation is what made the roles confusing — but
    /// when they do need in (a coach has left, something is broken), this grants it in one
    /// click and the team header then says so on every page until they hand it back.
    /// </summary>
    [HttpPost("teams/{teamId:int}/enter")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnterTeam(int teamId)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        await _access.GrantTeamAsync(HttpContext, team.Id, TeamAccessLevel.Manager,
            viaSiteAdmin: true);

        _log.LogInformation("Site admin took manager access to team {TeamId} ({Team})",
            team.Id, team.Name);

        return RedirectToAction("Index", "Coach", new { slug = team.Slug });
    }

    /// <summary>
    /// Issues a fresh pair of codes for a team and shows them once.
    ///
    /// This is the site admin's only route into a team, and it is deliberately indirect: they
    /// can hand out access, but they can't read a team's plans or roster without holding the
    /// code like anyone else. It is also the recovery path when a coach loses theirs.
    /// </summary>
    [HttpPost("teams/{teamId:int}/codes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateCodes(int teamId, string which)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        string notice;
        if (which == "manager")
        {
            var code = Security.NewAccessCode(8);
            team.CoachCodeHash = Security.HashCode(code);
            notice = $"{team.Name} — new manager code {code}. " +
                     "Anyone using the old one will need this instead.";
        }
        else
        {
            var code = Security.NewAccessCode();
            team.ViewCodeHash = Security.HashCode(code);
            notice = $"{team.Name} — new team code {code}. " +
                     "Every player and parent will need to enter this again.";
        }

        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { notice });
    }

    /// <summary>
    /// Removes a team and everything under it — plans, roster, view history, subscribers, and
    /// the team's directory on disk. Requires the team's name typed back as confirmation,
    /// because this is unrecoverable and a misclick would take a season of plans with it.
    /// </summary>
    [HttpPost("teams/{teamId:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTeam(int teamId, string confirmName)
    {
        if (!_access.IsSiteAdmin(User)) return Forbid();

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId);
        if (team is null) return NotFound();

        if (!string.Equals(confirmName?.Trim(), team.Name, StringComparison.OrdinalIgnoreCase))
            return View("Index", await BuildAsync(null,
                $"To delete \"{team.Name}\", type its name exactly to confirm."));

        var name = team.Name;

        // Cascades handle the child rows; the files on the volume are ours to clean up.
        _db.Teams.Remove(team);
        await _db.SaveChangesAsync();
        _storage.DeleteTeam(teamId);

        return RedirectToAction(nameof(Index), new { notice = $"Deleted {name} and all its plans." });
    }

    private async Task<AdminViewModel> BuildAsync(string? notice, string? error = null)
    {
        var teams = await _db.Teams
            .OrderBy(t => t.Name)
            .Select(t => new TeamSummary
            {
                Team = t,
                PlanCount = t.Plans.Count,
                PlayerCount = t.Players.Count
            })
            .ToListAsync();

        return new AdminViewModel
        {
            Teams = teams,
            Notice = notice,
            Error = error,
            Configured = _adminCodeHash is not null,
            UsedBytes = _storage.UsedBytes(),
            QuotaBytes = _storage.QuotaBytes
        };
    }

    private static string Slugify(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
