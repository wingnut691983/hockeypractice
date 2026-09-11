using HockeyPractice.Models;
using HockeyPractice.Persistence;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

public class HomeController : Controller
{
    /// <summary>
    /// The team offered at the end of /how-it-works, as a player would see it.
    ///
    /// Hardcoded rather than configured because a slug is not a secret: it is the visible part of
    /// every link to that team. The code that opens it is a secret, and is not here. It is read
    /// from the team's own row at request time, so rotating the demo team's code needs no change
    /// to this file and no redeploy.
    ///
    /// If the demo team is renamed or deleted, change this and the last panel follows. A slug that
    /// matches nothing simply leaves that panel out, so getting it wrong costs the panel, not the
    /// page.
    /// </summary>
    private const string DemoTeamSlug = "demoteam";

    private readonly AppDbContext _db;
    private readonly TeamAccessService _access;

    public HomeController(AppDbContext db, TeamAccessService access)
    {
        _db = db;
        _access = access;
    }

    public async Task<IActionResult> Index()
    {
        var teams = await _db.Teams.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();

        // Always show the list, even for a single team. Auto-forwarding saved players one tap
        // but removed the only page where the two ways in — read the plans, or manage the team
        // — are both visible. One tap is not the friction worth optimising away.
        var levels = teams.ToDictionary(t => t.Id, t => _access.RealLevelFor(User, t.Id));

        return View(new HomeViewModel
        {
            Teams = teams,
            Access = levels
        });
    }

    /// <summary>
    /// What has changed on the site, newest batch first.
    ///
    /// A page here rather than a link off to a document elsewhere: it needs no sign-in, it is
    /// reachable by every player and parent without anything being shared with them first, and
    /// it stays with the site it describes.
    /// </summary>
    [Route("whats-new")]
    public IActionResult WhatsNew() => View();

    /// <summary>
    /// What the site does, for someone who has not been given a code yet.
    ///
    /// It exists because the two audiences cannot both be previewed the same way. The demo team's
    /// code shows the player side, but there is no read-only manager mode, so showing anyone the
    /// coach side would mean handing over a manager code and with it the ability to wreck the
    /// demo team. The coach screens here are drawn, not real, which needs no access at all.
    ///
    /// Public and sign-in free for the same reasons as WhatsNew, and under the same content rule:
    /// everything on it is readable by anyone who finds the site.
    /// </summary>
    [Route("how-it-works")]
    public async Task<IActionResult> HowItWorks()
    {
        // No such team, or a team with no player code, and the last panel is simply left out. The
        // rest of the page is worth serving either way, so this never fails the request.
        var demo = await _db.Teams.FirstOrDefaultAsync(t => t.Slug == DemoTeamSlug);
        return View(demo?.ViewCode is { Length: > 0 } ? demo : null);
    }

    [Route("Home/Error")]
    public IActionResult Error(int? status)
    {
        ViewBag.Status = status;
        return View();
    }

    /// <summary>
    /// Web app manifest, so "Add to Home Screen" gives a real icon rather than a browser
    /// bookmark. Served from a controller because start_url has to carry the path prefix.
    /// </summary>
    [Route("site.webmanifest")]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Manifest()
    {
        var start = Url.Action("Index", "Home") ?? "/";
        return Json(new
        {
            name = "EBHockey Plans",
            short_name = "EBHockey",
            start_url = start,
            scope = start,
            display = "standalone",
            background_color = "#f4f6f9",
            theme_color = "#0B4EA2",
            // Was a single entry claiming the .ico was 48x48, which it never contained, and
            // nothing an Android launcher can install from. Two purposes, deliberately:
            // "any" is the full-bleed art for launchers that show it square, and "maskable" is
            // the padded one for the majority that crop to a circle. 13.6% of this artwork sits
            // outside an inscribed circle, so without the padded variant the corner marks are
            // sliced off on most phones.
            icons = new object[]
            {
                new { src = Url.Content("~/icon-192.png"), sizes = "192x192", type = "image/png", purpose = "any" },
                new { src = Url.Content("~/icon-512.png"), sizes = "512x512", type = "image/png", purpose = "any" },
                new { src = Url.Content("~/icon-maskable-512.png"), sizes = "512x512", type = "image/png", purpose = "maskable" }
            }
        });
    }
}
