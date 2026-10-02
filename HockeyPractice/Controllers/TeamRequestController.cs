using System.ComponentModel.DataAnnotations;
using System.Text;
using HockeyPractice.Models;
using HockeyPractice.Persistence;
using HockeyPractice.Services;
using HockeyPractice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HockeyPractice.Controllers;

/// <summary>
/// The public "ask for a team" form, linked from the home page.
///
/// Anonymous and ungated, which makes it the only write on this site a complete stranger can
/// reach. Everything about it is shaped by that: it writes one short row and sends one email, it
/// can create nothing else, and it is rate limited and honeypotted. It is deliberately NOT a
/// self-serve team creation: a team needs a slug chosen and codes issued, which stays a site-admin
/// job, so what this produces is a message to act on rather than a team.
/// </summary>
[Route("request-a-team")]
public class TeamRequestController : Controller
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<TeamRequestController> _log;

    public TeamRequestController(AppDbContext db, NotificationService notifications,
        ILogger<TeamRequestController> log)
    {
        _db = db;
        _notifications = notifications;
        _log = log;
    }

    [HttpGet("")]
    public IActionResult Index() => View("Request", new TeamRequestFormViewModel());

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("team-request")]
    public async Task<IActionResult> Submit(TeamRequestFormViewModel form)
    {
        // Honeypot. A field no human sees, so anything in it came from something filling every
        // input on the page. Answered with the same thank-you a real submission gets, and saved
        // nowhere: an error would teach the bot what to avoid next time.
        if (!string.IsNullOrWhiteSpace(form.Website))
        {
            _log.LogInformation("Discarded a team request that filled the honeypot field.");
            return View("RequestSent");
        }

        var request = new TeamRequest
        {
            FirstName = Clean(form.FirstName),
            LastName = Clean(form.LastName),
            Email = Clean(form.Email),
            Association = Clean(form.Association),
            TeamName = Clean(form.TeamName)
        };

        // Lengths are checked here rather than left to the model's MaxLength attributes, because
        // SQLite does not enforce them and these five values arrive from an anonymous stranger.
        // Refused rather than silently truncated: a name cut in half is worse than being asked.
        var error =
            Required(request.FirstName, "first name", 80) ??
            Required(request.LastName, "last name", 80) ??
            Required(request.Association, "association or club", 140) ??
            Required(request.TeamName, "team name", 140) ??
            Required(request.Email, "email address", 200) ??
            (new EmailAddressAttribute().IsValid(request.Email)
                ? null
                : "That email address doesn't look right. We need it to reach you.");

        if (error is not null)
        {
            form.Error = error;
            return View("Request", form);
        }

        // Saved BEFORE the email, and the email failure is swallowed below. A request that
        // reached the database but not the inbox still shows on the admin page; one that was
        // lost because mail was down is gone with nothing to show the person who typed it.
        _db.TeamRequests.Add(request);
        await _db.SaveChangesAsync();

        try
        {
            var adminUrl = Url.Action("Index", "SiteAdmin", null, Request.Scheme)!;
            await _notifications.NotifyTeamRequestedAsync(request, adminUrl);
        }
        catch (Exception ex)
        {
            // Never let the notification decide whether the request was accepted.
            _log.LogWarning("Could not email the team request {RequestId}: {Type}: {Error}",
                request.Id, ex.GetType().Name, ex.Message);
        }

        return View("RequestSent");
    }

    /// <summary>
    /// Trims, and collapses every run of whitespace, control characters included, to one space.
    ///
    /// A browser cannot put a newline in a text input, but a crafted POST can, and these values
    /// go straight into an email subject and a Reply-To header. Resend takes them as JSON rather
    /// than as raw SMTP, so a newline is not an injection there, but relying on a third party to
    /// sanitise what we hand it is the wrong place to draw the line. It also keeps a multi-line
    /// "association" from pulling the admin page about.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (builder.Length > 0 && !lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private static string? Required(string value, string label, int max) =>
        value.Length == 0 ? $"Please fill in your {label}."
        : value.Length > max ? $"That {label} is too long. Keep it under {max} characters."
        : null;
}
