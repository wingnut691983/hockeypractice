using System.Threading.Channels;

namespace HockeyPractice.Services;

/// <summary>
/// One plan's worth of "tell the subscribers", with everything the background sender cannot work
/// out for itself.
///
/// The scheme, host and path base are captured in the request and carried, because the emailed
/// links are absolute and there is no <c>HttpContext</c> on the other side of the queue to build
/// them from. Carrying them rather than configuring a base URL keeps the existing behaviour
/// exactly: links point at whatever host the coach published from. The one consequence worth
/// knowing is that publishing from the bare upturtle.app URL puts that host in the email, which
/// was true before this queue existed too.
/// </summary>
public record PublishNotification(
    int PlanId, int TeamId, string Scheme, string Host, string PathBase);

/// <summary>
/// Hands a published plan's notifications to <see cref="PublishNotificationService"/> so the
/// coach's request does not wait on them.
///
/// Why this exists: <c>NotifyPublishedAsync</c> sends one message per subscriber, each with a
/// 15 second timeout in <see cref="ResendEmailSender"/>, and <c>Publish</c> used to await the
/// whole loop. Thirty subscribers against a degraded provider is 7.5 minutes of a held request,
/// ending in a gateway error that tells the coach nothing about whether the plan published. It
/// had: <c>Status</c> and <c>PublishedUtc</c> are saved before the mail runs, so the ambiguity
/// was the worst part of it.
///
/// Bounded and <see cref="BoundedChannelFullMode.DropWrite"/> on purpose. An unbounded queue
/// turns a wedged provider into memory growth, and silently dropping the hundred-and-first plan
/// publish of a single pod's lifetime is a better failure than that. <c>TryEnqueue</c> reports
/// it so the caller can log rather than guess.
///
/// In memory and per process, like <c>MaintenanceState</c>: a queued notification does not
/// survive a restart. That is a deliberate limit, not an oversight. A plan's notification is
/// worth sending in the seconds after a publish and not worth a durable outbox, and the site
/// runs one replica. A second replica would not break this, it would just mean each pod only
/// drains its own publishes, which is already how it behaves.
/// </summary>
public class PublishNotificationQueue
{
    private readonly Channel<PublishNotification> _channel =
        Channel.CreateBounded<PublishNotification>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

    /// <summary>Queues a job. False means the queue was full and nothing will be sent.</summary>
    public bool TryEnqueue(PublishNotification job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<PublishNotification> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
