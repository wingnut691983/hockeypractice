using HockeyPractice.Persistence;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

public class HomeController : Controller
{
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
