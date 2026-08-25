using System.Net;
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
    private readonly ILogger<NotificationService> _log;

    public NotificationService(AppDbContext db, IEmailSender email, ILogger<NotificationService> log)
    {
        _db = db;
        _email = email;
        _log = log;
    }

    public bool IsLive => _email.IsLive;

    public Task SendConfirmationAsync(Team team, Subscriber subscriber, string confirmUrl)
    {
        var subject = $"Confirm practice plan emails for {team.Name}";
        var text =
            $"Someone asked to get an email whenever a new {team.Name} practice plan is posted.\n\n" +
            $"Confirm here:\n{confirmUrl}\n\n" +
            "If that wasn't you, ignore this message — nothing will be sent.";

        var html = Wrap(team,
            $"<p>Someone asked to get an email whenever a new <strong>{Esc(team.Name)}</strong> " +
            "practice plan is posted.</p>" +
            $"<p>{Button(confirmUrl, "Confirm", team)}</p>" +
            "<p style=\"color:#5c6879;font-size:14px\">If that wasn't you, ignore this message — " +
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

            if (await _email.SendAsync(subscriber.Email, subject, html, text)) sent++;
        }

        _log.LogInformation("Notified {Sent} of {Total} subscribers about plan {PlanId}",
            sent, subscribers.Count, plan.Id);
    }

    private static string Button(string url, string label, Team team) =>
        $"<a href=\"{Esc(url)}\" style=\"display:inline-block;background:{Esc(team.PrimaryColor)};" +
        "color:#fff;text-decoration:none;font-weight:700;padding:12px 20px;border-radius:10px\">" +
        $"{Esc(label)}</a>";

    private static string Wrap(Team team, string body) =>
        "<div style=\"font-family:system-ui,-apple-system,'Segoe UI',sans-serif;font-size:16px;" +
        "line-height:1.5;color:#12161d;max-width:520px\">" +
        $"<div style=\"height:4px;background:{Esc(team.PrimaryColor)};border-radius:2px\"></div>" +
        body +
        "</div>";

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
