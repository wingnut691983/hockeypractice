using HockeyPractice.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var teams = await _db.Teams.OrderBy(t => t.Name).ToListAsync();

        // The common case is one team — skip the picker entirely rather than making players
        // tap through a list of one.
        if (teams.Count == 1)
            return RedirectToAction("Index", "Team", new { slug = teams[0].Slug });

        return View(teams);
    }

    [Route("Home/Error")]
    public IActionResult Error() => View();

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
            name = "Practice Plans",
            short_name = "Practice",
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
