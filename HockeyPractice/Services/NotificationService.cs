using System.Net;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;
using HockeyPractice.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Services;

/// <summary>
/// Builds and sends the two messages this site needs: a double-opt-in confirmation, and the
/// "new plan posted" notice. Kept plain-text-first — these land on phones, in Gmail's clipped
/// view, and nobody needs a newsletter layout to tap one link.
/// </summary>
public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(AppDbContext db, IEmailSender email, IConfiguration config,
        ILogger<NotificationService> log)
    {
        _db = db;
        _email = email;
        _config = config;
        _log = log;
    }

    public bool IsLive => _email.IsLive;

    /// <summary>
    /// Tells the operator a stranger has asked for a team. Config, not a constant, because the
    /// one address this sends to is a person rather than a property of the code, and because
    /// changing where the site's own mail lands should not need a deploy.
    ///
    /// Missing SITE_ADMIN_EMAIL is a no-op that logs, not a failure. The request is already saved
    /// by the time this runs and the admin page shows it either way, so the worst case is the
    /// operator finds out when they next sign in rather than in their inbox. The same shape as
    /// the backup store: a capability that is absent rather than a rule to remember.
    /// </summary>
    public async Task NotifyTeamRequestedAsync(TeamRequest request, string adminUrl)
    {
        var to = _config["SITE_ADMIN_EMAIL"];
        if (string.IsNullOrWhiteSpace(to))
        {
            _log.LogInformation(
                "Team request {RequestId} saved, but SITE_ADMIN_EMAIL is not set so nobody was emailed.",
                request.Id);
            return;
        }

        // The prefix is shouted on purpose: this lands in a personal inbox alongside everything
        // else, and it is the one message from this site the operator has to act on rather than
        // read. It also gives them something exact to filter on.
        var subject = $"EBHOCKEYPLAN TEAM REQUEST: {request.TeamName} ({request.Association})";

        // Their address goes in Reply-To rather than From: From has to stay on the verified
        // sending domain or the message is rejected, and the one thing the operator will want to
        // do with this mail is answer the person who sent it.
        var headers = new Dictionary<string, string> { ["Reply-To"] = request.Email };

        var text =
            $"{request.FullName} asked for a team on EBHockey Plans.\n\n" +
            $"Name:        {request.FullName}\n" +
            $"Email:       {request.Email}\n" +
            $"Association: {request.Association}\n" +
            $"Team:        {request.TeamName}\n\n" +
            $"Create it, or mark it handled, in the site admin:\n{adminUrl}\n\n" +
            "Replying to this message goes straight back to them.";

        var html =
            "<div style=\"font-family:system-ui,-apple-system,'Segoe UI',sans-serif;font-size:16px;" +
            "line-height:1.5;color:#12161d;max-width:520px\">" +
            $"<p><strong>{Esc(request.FullName)}</strong> asked for a team on EBHockey Plans.</p>" +
            "<table style=\"border-collapse:collapse;font-size:15px\">" +
            Row("Email", $"<a href=\"mailto:{Esc(request.Email)}\">{Esc(request.Email)}</a>") +
            Row("Association", Esc(request.Association)) +
            Row("Team", Esc(request.TeamName)) +
            "</table>" +
            $"<p><a href=\"{Esc(adminUrl)}\">Open the site admin</a></p>" +
            "<p style=\"color:#8b95a7;font-size:12px\">Replying to this message goes straight " +
            "back to them.</p></div>";

        await _email.SendAsync(to, subject, html, text, headers);
    }

    private static string Row(string label, string value) =>
        $"<tr><td style=\"padding:2px 14px 2px 0;color:#5c6879\">{Esc(label)}</td>" +
        $"<td style=\"padding:2px 0\">{value}</td></tr>";

    public Task SendConfirmationAsync(Team team, Subscriber subscriber, string confirmUrl)
    {
        var subject = $"Confirm practice plan emails for {team.Name}";
        var text =
            $"Someone asked to get an email whenever a new {team.Name} practice plan is posted.\n\n" +
            $"Confirm here:\n{confirmUrl}\n\n" +
            "If that wasn't you, ignore this message. Nothing will be sent.";

        var html = Wrap(team,
            $"<p>Someone asked to get an email whenever a new <strong>{Esc(team.Name)}</strong> " +
            "practice plan is posted.</p>" +
            $"<p>{Button(confirmUrl, "Confirm", team)}</p>" +
            "<p style=\"color:#5c6879;font-size:14px\">If that wasn't you, ignore this message. " +
            "nothing will be sent.</p>");

        return _email.SendAsync(subscriber.Email, subject, html, text);
    }

    /// <summary>
    /// Notifies confirmed subscribers that a plan is live. Failures are logged and swallowed:
    /// a publish must succeed even when mail is down.
    /// </summary>
    public async Task NotifyPublishedAsync(Team team, PracticePlan plan, string planUrl,
        Func<Subscriber, string> unsubscribeUrl)
    {
        var subscribers = await _db.Subscribers
            .Where(s => s.TeamId == team.Id && s.ConfirmedUtc != null)
            .ToListAsync();

        if (subscribers.Count == 0) return;

        var sent = 0;
        foreach (var subscriber in subscribers)
        {
            var unsub = unsubscribeUrl(subscriber);
            var subject = $"{team.Name}: {plan.Title}";

            var text =
                $"A new practice plan is up for {team.Name}.\n\n" +
                $"{plan.Title}\n{plan.PracticeDateLocal:dddd, MMMM d} at {plan.PracticeDateLocal:h:mm tt}" +
                (string.IsNullOrWhiteSpace(plan.Location) ? "" : $"\n{plan.Location}") +
                $"\n\nOpen it here:\n{planUrl}\n\n" +
                $"Stop these emails: {unsub}";

            var html = Wrap(team,
                $"<p>A new practice plan is up for <strong>{Esc(team.Name)}</strong>.</p>" +
                $"<h2 style=\"margin:.4em 0;font-size:20px\">{Esc(plan.Title)}</h2>" +
                $"<p style=\"color:#5c6879;margin-top:0\">{plan.PracticeDateLocal:dddd, MMMM d} " +
                $"at {plan.PracticeDateLocal:h:mm tt}" +
                (string.IsNullOrWhiteSpace(plan.Location) ? "" : $" · {Esc(plan.Location!)}") +
                "</p>" +
                $"<p>{Button(planUrl, "See the plan", team)}</p>" +
                $"<p style=\"color:#8b95a7;font-size:12px\">" +
                $"<a href=\"{Esc(unsub)}\" style=\"color:#8b95a7\">Stop these emails</a></p>");

            // RFC 8058 one-click. The URL is angle-bracketed because the header is a list, and
            // the Post header is what lets the client POST to it instead of following a GET: the
            // GET is now a confirmation page on purpose (see SubscriptionController.Unsubscribe),
            // so without this pair a mail client's unsubscribe button would land a human on a
            // page asking them to press another button.
            var headers = new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{unsub}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
            };

            if (await _email.SendAsync(subscriber.Email, subject, html, text, headers)) sent++;
        }

        _log.LogInformation("Notified {Sent} of {Total} subscribers about plan {PlanId}",
            sent, subscribers.Count, plan.Id);
    }

    // Same contrast problem as the site: a team with light colours got an unreadable button.
    // Email clients have no custom properties, so the colour is resolved here instead.
    private static string Button(string url, string label, Team team) =>
        $"<a href=\"{Esc(url)}\" style=\"display:inline-block;background:{Esc(team.SafePrimary)};" +
        $"color:{Palette.On(team.SafePrimary)};text-decoration:none;font-weight:700;" +
        "padding:12px 20px;border-radius:10px\">" +
        $"{Esc(label)}</a>";

    private static string Wrap(Team team, string body) =>
        "<div style=\"font-family:system-ui,-apple-system,'Segoe UI',sans-serif;font-size:16px;" +
        "line-height:1.5;color:#12161d;max-width:520px\">" +
        $"<div style=\"height:4px;background:{Esc(team.SafePrimary)};border-radius:2px\"></div>" +
        body +
        "</div>";

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
