using System.ComponentModel.DataAnnotations;
using HockeyPractice.Models;
using HockeyPractice.Persistence;
using HockeyPractice.Services;
using HockeyPractice.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Controllers;

/// <summary>
/// Opt-in email notifications. Double opt-in and self-serve by design: the coach never uploads
/// a list of children's email addresses, and every address here belongs to whoever typed it.
/// </summary>
public class SubscriptionController : TeamScopedController
{
    private readonly NotificationService _notifications;

    public SubscriptionController(AppDbContext db, TeamAccessService access, NotificationService notifications)
        : base(db, access) => _notifications = notifications;

    [HttpPost("t/{slug}/subscribe")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("code-entry")]
    public async Task<IActionResult> Subscribe(string slug, string email)
    {
        var (ctx, failure) = await ResolveAsync(slug, TeamAccessLevel.Player);
        if (failure is not null) return failure;

        var address = (email ?? string.Empty).Trim();
        if (!new EmailAddressAttribute().IsValid(address) || address.Length > 200)
            return Redirect(Url.Action("Plans", "Team", new { slug, sub = "invalid" }) + "#heads-up");

        var existing = await Db.Subscribers
            .FirstOrDefaultAsync(s => s.TeamId == ctx!.Team.Id && s.Email == address);

        if (existing is { ConfirmedUtc: not null })
            return Redirect(Url.Action("Plans", "Team", new { slug, sub = "already" }) + "#heads-up");

        var subscriber = existing ?? new Subscriber
        {
            TeamId = ctx!.Team.Id,
            Email = address,
            UnsubToken = Security.NewToken()
        };

        // A fresh token each time, so an old confirmation link stops working.
        subscriber.ConfirmToken = Security.NewToken();
        subscriber.PlayerId = ctx!.Me?.Id;

        if (existing is null) Db.Subscribers.Add(subscriber);
        await Db.SaveChangesAsync();

        var confirmUrl = Url.Action(nameof(Confirm), "Subscription",
            new { token = subscriber.ConfirmToken }, Request.Scheme)!;
        await _notifications.SendConfirmationAsync(ctx.Team, subscriber, confirmUrl);

        return Redirect(Url.Action("Plans", "Team", new { slug, sub = "check" }) + "#heads-up");
    }

    [HttpGet("s/confirm/{token}")]
    public async Task<IActionResult> Confirm(string token)
    {
        var subscriber = await Db.Subscribers
            .Include(s => s.Team)
            .FirstOrDefaultAsync(s => s.ConfirmToken == token);

        if (subscriber is null)
            return View("SubscriptionResult", ("That link has expired.",
                "Ask for a new confirmation email from your team's page."));

        if (subscriber.ConfirmedUtc is null)
        {
            subscriber.ConfirmedUtc = DateTime.UtcNow;
            await Db.SaveChangesAsync();
        }

        return View("SubscriptionResult", ("You're all set.",
            $"You'll get an email whenever a new {subscriber.Team?.Name} practice plan is posted."));
    }

    /// <summary>
    /// Asks before unsubscribing. **This GET must stay read-only.**
    ///
    /// It used to delete the row outright, which read as kindness: an unsubscribe that asks
    /// follow-up questions is the reason people mark mail as spam instead. The problem is who
    /// follows links in a message body. Corporate link scanners and mail client prefetchers do,
    /// without anyone clicking, so a parent could be unsubscribed by their own employer's security
    /// appliance and never find out: the mail simply stops and the site looks broken. Nothing in
    /// the app would record it either, because deleting the row is indistinguishable from the
    /// parent having meant it.
    ///
    /// The one-click experience is not lost. The mail carries List-Unsubscribe-Post, so a mail
    /// client's own unsubscribe button POSTs straight to <see cref="UnsubscribeConfirmed"/> and
    /// never renders this page. A human who taps the link in the body gets one button.
    /// </summary>
    [HttpGet("s/unsub/{token}")]
    public async Task<IActionResult> Unsubscribe(string token)
    {
        var subscriber = await Db.Subscribers
            .Include(s => s.Team)
            .FirstOrDefaultAsync(s => s.UnsubToken == token);

        // A spent or unknown token is not an error worth a scary page: the common cause is
        // unsubscribing twice, and the outcome they wanted is already true.
        if (subscriber is null)
            return View("SubscriptionResult", ("You're already unsubscribed.",
                "There are no practice plan emails going to that address. You can sign up again " +
                "any time from your team's page."));

        return View("Unsubscribe", (token, subscriber.Email, subscriber.Team?.Name ?? "this team"));
    }

    /// <summary>
    /// Actually unsubscribes. Reached two ways: the button on the page above, and a mail client's
    /// own one-click unsubscribe, which RFC 8058 defines as a POST to the List-Unsubscribe URL.
    ///
    /// No antiforgery token, and that is required rather than convenient. The one-click POST
    /// arrives from Gmail's or Yahoo's infrastructure with no cookie and no form, so there is
    /// nothing to validate against. The unguessable token in the URL is the authorisation, the
    /// same way the confirm link is: 24 bytes of CSPRNG from <c>Security.NewToken</c>. The attack
    /// this gives up is a cross-site POST that unsubscribes someone, which needs their token,
    /// which is the whole secret anyway.
    /// </summary>
    [HttpPost("s/unsub/{token}")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> UnsubscribeConfirmed(string token)
    {
        var subscriber = await Db.Subscribers.FirstOrDefaultAsync(s => s.UnsubToken == token);

        if (subscriber is not null)
        {
            Db.Subscribers.Remove(subscriber);
            await Db.SaveChangesAsync();
        }

        return View("SubscriptionResult", ("Unsubscribed.",
            "You won't get any more practice plan emails. You can sign up again any time."));
    }
}
