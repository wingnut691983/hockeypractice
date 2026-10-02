using HockeyPractice.Models;
using HockeyPractice.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Services;

/// <summary>
/// Drains <see cref="PublishNotificationQueue"/> and sends each published plan's subscriber mail,
/// off the coach's request. See the queue for why.
///
/// Everything here is wrapped. <c>BackgroundServiceExceptionBehavior</c> has defaulted to
/// <c>StopHost</c> since .NET 6, so an unhandled exception in this loop would take the site down
/// because an email failed, which is the same trap <c>ScheduledBackupService</c> documents. The
/// per-job catch is what keeps one bad plan from ending the loop for every later one.
/// </summary>
public class PublishNotificationService : BackgroundService
{
    private readonly PublishNotificationQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly LinkGenerator _links;
    private readonly ILogger<PublishNotificationService> _log;

    public PublishNotificationService(PublishNotificationQueue queue, IServiceScopeFactory scopes,
        LinkGenerator links, ILogger<PublishNotificationService> log)
    {
        _queue = queue;
        _scopes = scopes;
        _links = links;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // An await before anything that could throw, for the reason ScheduledBackupService spells
        // out: ExecuteAsync runs inline until its first await, so a synchronous throw here comes
        // out of StartAsync and stops the host rather than failing a send.
        await Task.Yield();

        try
        {
            await foreach (var job in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await SendAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down mid-send. The plan is published either way.
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not notify subscribers about plan {PlanId}: {Type}: {Error}",
                        job.PlanId, ex.GetType().Name, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            // The loop itself failed, which should not be possible. Log and let the service end
            // quietly; the site keeps serving and plans keep publishing without mail.
            _log.LogError("The publish notification loop stopped: {Type}: {Error}",
                ex.GetType().Name, ex.Message);
        }
    }

    private async Task SendAsync(PublishNotification job, CancellationToken ct)
    {
        // Its own scope: the request that queued this is long gone, and so is its DbContext.
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == job.TeamId, ct);
        if (team is null) return;

        // Re-read rather than carrying the entity across the queue, and re-check the status: a
        // coach who publishes and immediately unpublishes has changed their mind, and the mail
        // has not gone yet, so the only sensible reading is not to send it.
        var plan = await db.Plans.FirstOrDefaultAsync(
            p => p.Id == job.PlanId && p.TeamId == job.TeamId, ct);

        if (plan is null || plan.Status != PlanStatus.Published)
        {
            _log.LogInformation(
                "Skipped notifications for plan {PlanId}: it is no longer a published plan of this team.",
                job.PlanId);
            return;
        }

        var host = new HostString(job.Host);
        var pathBase = new PathString(string.IsNullOrEmpty(job.PathBase) ? null : job.PathBase);

        // LinkGenerator, not Url.Action: the same routes, resolved without an HttpContext.
        var planUrl = _links.GetUriByAction(
            action: "Details", controller: "Plan",
            values: new { slug = team.Slug, id = plan.Id },
            scheme: job.Scheme, host: host, pathBase: pathBase)!;

        await notifications.NotifyPublishedAsync(team, plan, planUrl,
            s => _links.GetUriByAction(
                action: nameof(Controllers.SubscriptionController.Unsubscribe), controller: "Subscription",
                values: new { token = s.UnsubToken },
                scheme: job.Scheme, host: host, pathBase: pathBase)!);
    }
}
