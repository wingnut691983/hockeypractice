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
            return RedirectToAction("Plans", "Team", new { slug, sub = "invalid" });

        var existing = await Db.Subscribers
            .FirstOrDefaultAsync(s => s.TeamId == ctx!.Team.Id && s.Email == address);

        if (existing is { ConfirmedUtc: not null })
            return RedirectToAction("Plans", "Team", new { slug, sub = "already" });

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

        return RedirectToAction("Plans", "Team", new { slug, sub = "check" });
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
    /// One click, no confirmation step — an unsubscribe that asks follow-up questions is the
    /// reason people mark mail as spam instead.
    /// </summary>
    [HttpGet("s/unsub/{token}")]
    public async Task<IActionResult> Unsubscribe(string token)
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
