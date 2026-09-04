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
            icons = new[]
            {
                new { src = Url.Content("~/favicon.ico"), sizes = "48x48", type = "image/x-icon" }
            }
        });
    }
}
