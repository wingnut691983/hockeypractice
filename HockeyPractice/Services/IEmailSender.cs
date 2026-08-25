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

    public LoggingEmailSender(ILogger<LoggingEmailSender> log) => _log = log;

    public bool IsLive => false;

    public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody,
        CancellationToken ct = default)
    {
        _log.LogInformation("[Email:not-sent] To={To} Subject={Subject}\n{Body}",
            toEmail, subject, textBody);
        return Task.FromResult(true);
    }
}
