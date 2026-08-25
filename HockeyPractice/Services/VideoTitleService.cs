using System.Text.Json;
using HockeyPractice.Models;

namespace HockeyPractice.Services;

/// <summary>
/// Looks up real video titles via oEmbed — no API key, no quota, one GET per video.
///
/// This fills the gap the PDF can't: a bare URL on its own line has nothing in the document
/// to be named after, and "Video 1" is useless to a player. It is strictly best-effort —
/// egress may be blocked or slow, and an upload must never fail because YouTube didn't answer.
/// </summary>
public class VideoTitleService
{
    // Per-request budget. Long enough for a normal response, short enough that a coach
    // uploading a plan with six videos isn't left staring at a spinner.
    private static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(12);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<VideoTitleService> _log;

    public VideoTitleService(IHttpClientFactory http, ILogger<VideoTitleService> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Fills in <see cref="PlanLink.VideoTitle"/> for every recognised video, in parallel.
    /// Links we can't identify or can't reach are left untouched.
    /// </summary>
    public async Task PopulateTitlesAsync(IReadOnlyCollection<PlanLink> links, CancellationToken ct = default)
    {
        var targets = links.Where(l => OEmbedEndpoint(l) is not null).ToList();
        if (targets.Count == 0) return;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(OverallBudget);

        try
        {
            await Task.WhenAll(targets.Select(link => FetchOneAsync(link, budget.Token)));
        }
        catch (Exception ex)
        {
            // WhenAll surfaces the first failure; individual failures are already swallowed
            // inside FetchOneAsync, so reaching here means something systemic.
            _log.LogDebug("Video title lookup ended early: {Error}", ex.Message);
        }
    }

    private async Task FetchOneAsync(PlanLink link, CancellationToken ct)
    {
        var endpoint = OEmbedEndpoint(link);
        if (endpoint is null) return;

        try
        {
            using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perRequest.CancelAfter(PerRequestTimeout);

            var client = _http.CreateClient();
            // Some providers reject requests without a UA.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HockeyPractice/1.0");

            using var response = await client.GetAsync(endpoint, perRequest.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 401/403 on YouTube means the video is private or age-restricted; 404 means
                // it's gone. Either way the coach should hear about it from the card, not a log.
                _log.LogDebug("oEmbed {Status} for {Url}", (int)response.StatusCode, link.Url);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(perRequest.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: perRequest.Token);

            if (json.RootElement.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String)
            {
                link.VideoTitle = Tidy(title.GetString());
            }
        }
        catch (Exception ex)
        {
            // Timeout, DNS failure, blocked egress, malformed JSON — all non-fatal.
            _log.LogDebug("Could not fetch a title for {Url}: {Error}", link.Url, ex.Message);
        }
    }

    private static string? OEmbedEndpoint(PlanLink link)
    {
        if (link.VideoId is null) return null;

        return link.Kind switch
        {
            LinkKind.YouTube =>
                "https://www.youtube.com/oembed?format=json&url=" +
                Uri.EscapeDataString($"https://www.youtube.com/watch?v={link.VideoId}"),
            LinkKind.Vimeo =>
                "https://vimeo.com/api/oembed.json?url=" +
                Uri.EscapeDataString($"https://vimeo.com/{link.VideoId}"),
            _ => null
        };
    }

    private static string? Tidy(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var text = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ").Trim();

        // Channel branding tacked on the end reads as noise on a small card.
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\s*[|｜]\s*[^|｜]{1,40}$", "").Trim();

        if (text.Length < 2) return null;
        return text.Length > 140 ? text[..140].TrimEnd() : text;
    }
}
