using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HockeyPractice.Services;

/// <summary>
/// Resend over an SDK: it's one authenticated POST, so a dependency would buy nothing.
/// Selected at startup only when RESEND_API_KEY is present — otherwise mail is logged.
/// </summary>
public class ResendEmailSender : IEmailSender
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ResendEmailSender> _log;
    private readonly string _apiKey;
    private readonly string _from;

    public ResendEmailSender(IHttpClientFactory http, IConfiguration config, ILogger<ResendEmailSender> log)
    {
        _http = http;
        _log = log;
        _apiKey = config["RESEND_API_KEY"] ?? string.Empty;
        // Overridable so the team can send from their own verified domain.
        _from = config["EMAIL_FROM"] ?? "Practice Plans <onboarding@resend.dev>";
    }

    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool IsLive => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, string textBody,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            // The headers property is left OUT of the JSON when there is nothing to send, not sent
            // as null and not as {}. The confirmation mail is transactional and has nothing to
            // unsubscribe from, so it should carry no List-Unsubscribe at all, and an API that
            // type-checks its optional fields is entitled to reject a null. Measured: without
            // WhenWritingNull the payload carried "headers": null.
            var response = await client.PostAsJsonAsync("https://api.resend.com/emails", new
            {
                from = _from,
                to = new[] { toEmail },
                subject,
                html = htmlBody,
                text = textBody,
                headers = headers is { Count: > 0 } ? headers : null
            }, OmitNulls, ct);

            if (response.IsSuccessStatusCode) return true;

            // Log the status, never the body — a provider error can echo the recipient address.
            _log.LogWarning("Resend rejected a message to a subscriber: {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            // A mail failure must never take down a publish.
            _log.LogWarning("Could not send email: {Type}: {Error}", ex.GetType().Name, ex.Message);
            return false;
        }
    }
}
