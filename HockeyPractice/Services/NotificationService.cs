using System.Net;
using HockeyPractice.Infrastructure;
using HockeyPractice.Models;

namespace HockeyPractice.Services;

/// <summary>
/// The one message this site sends: somebody has asked for a team.
///
/// It used to also carry subscriber mail, a double-opt-in confirmation and a "new plan posted"
/// notice. That whole mechanism was removed on 2 October 2026 before it was ever switched on,
/// deliberately rather than by neglect. See README's "What I'd flag" for what has to come back
/// with it if it ever returns, in particular that the publish send cannot sit in the request.
/// </summary>
public class NotificationService
{
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(IEmailSender email, IConfiguration config,
        ILogger<NotificationService> log)
    {
        _email = email;
        _config = config;
        _log = log;
    }

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

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
