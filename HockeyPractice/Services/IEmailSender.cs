namespace HockeyPractice.Services;

public interface IEmailSender
{
    /// <summary>True when a real provider is wired up; false when mail is only being logged.</summary>
    bool IsLive { get; }

    Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken ct = default);
}

/// <summary>
/// Development / unconfigured fallback: writes the mail to the log instead of sending it.
/// Keeps the whole subscribe → confirm → notify flow exercisable before a sending domain
/// exists, and guarantees local development never mails a real family.
/// </summary>
public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _log;
    private readonly bool _logBodies;

    public LoggingEmailSender(ILogger<LoggingEmailSender> log, IHostEnvironment environment)
    {
        _log = log;
        // The body is what makes this sender useful locally: the confirm and unsubscribe links
        // only exist inside it, so there is no other way to walk the flow without a provider.
        // It is also why the body must NOT be logged anywhere else. This sender is selected
        // whenever RESEND_API_KEY is unset, which includes production with the key forgotten,
        // and the body carries the confirm token, the unsubscribe token and the subscriber's
        // address in plain text. Anyone who can read the log could then confirm subscriptions
        // and unsubscribe arbitrary addresses.
        _logBodies = environment.IsDevelopment();
    }

    public bool IsLive => false;

    public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken ct = default)
    {
        if (_logBodies)
        {
            _log.LogInformation("[Email:not-sent] To={To} Subject={Subject}\n{Body}",
                toEmail, subject, textBody);
        }
        else
        {
            // Subject only. It carries the team name and plan title, which are not secrets, and
            // it is enough to tell that a send was attempted and which one. The recipient is left
            // out for the same reason ResendEmailSender never logs a provider's response body.
            _log.LogInformation("[Email:not-sent] Subject={Subject}", subject);
        }

        return Task.FromResult(true);
    }
}
