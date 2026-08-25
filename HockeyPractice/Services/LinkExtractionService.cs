using System.Text.RegularExpressions;
using HockeyPractice.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace HockeyPractice.Services;

/// <summary>
/// Pulls video links out of an uploaded practice plan so players get tappable cards instead of
/// having to hit a tiny hyperlink inside a PDF on a phone.
///
/// Two passes, because coaches produce both kinds: real link annotations (a hyperlink in Word)
/// and bare URLs typed as plain text (pasting a YouTube link doesn't always linkify).
/// </summary>
public partial class LinkExtractionService
{
    private readonly ILogger<LinkExtractionService> _log;

    public LinkExtractionService(ILogger<LinkExtractionService> log) => _log = log;

    [GeneratedRegex(@"https?://[^\s<>""')\]}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubePattern();

    [GeneratedRegex(@"vimeo\.com/(?:video/)?(\d{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex VimeoPattern();

    /// <summary>A URL as found, with whatever the document says about it.</summary>
    private sealed record Found(string Url, string? Anchor, string? Section);

    public List<PlanLink> Extract(string pdfPath)
    {
        var found = new List<Found>();

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            foreach (var page in document.GetPages())
            {
                PdfTextMap map;
                try
                {
                    map = PdfTextMap.Build(page);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("Could not map page text: {Error}", ex.Message);
                    continue;
                }

                CollectAnnotationLinks(page, map, found);
                CollectTextLinks(page, map, found);
            }
        }
        catch (Exception ex)
        {
            // Extraction is a convenience, never a gate on publishing. A PDF we can't parse
            // still uploads and still renders.
            _log.LogWarning("Could not extract links from {Path}: {Error}", pdfPath, ex.Message);
            return new List<PlanLink>();
        }

        return Normalize(found);
    }

    /// <summary>
    /// Real hyperlinks. The anchor text is the best label available — it is literally what the
    /// coach chose to call the drill when they inserted the link.
    /// </summary>
    private void CollectAnnotationLinks(Page page, PdfTextMap map, List<Found> found)
    {
        try
        {
            foreach (var annotation in page.GetAnnotations())
            {
                if (annotation.Action is not UriAction uri || string.IsNullOrWhiteSpace(uri.Uri))
                    continue;

                var rect = annotation.Rectangle;
                var anchor = map.TextWithin(rect, page);

                // When the visible text is the URL itself there is no anchor worth keeping;
                // fall through to the line it sits on.
                if (anchor is not null && LooksLikeUrl(anchor))
                    anchor = null;

                var lineIndex = map.IndexOfLineAt(rect.Top, rect.Bottom);
                anchor ??= StripUrls(map.TextOfLine(lineIndex));

                found.Add(new Found(uri.Uri, PdfTextMap.Tidy(anchor), map.SectionFor(lineIndex)));
            }
        }
        catch (Exception ex)
        {
            // A malformed annotation shouldn't cost us the rest of the page.
            _log.LogDebug("Annotation read failed on a page: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// URLs typed as plain text — pasting a YouTube link into Word doesn't always linkify it.
    /// Here the surrounding line is all the context there is.
    /// </summary>
    private static void CollectTextLinks(Page page, PdfTextMap map, List<Found> found)
    {
        foreach (var line in map.Lines)
        {
            foreach (Match m in UrlPattern().Matches(line.Text))
            {
                var index = map.IndexOfLineContaining(m.Value);
                found.Add(new Found(
                    m.Value,
                    PdfTextMap.Tidy(StripUrls(line.Text)),
                    map.SectionFor(index)));
            }
        }
    }

    private static string? StripUrls(string? text) =>
        text is null ? null : UrlPattern().Replace(text, " ").Trim();

    private static bool LooksLikeUrl(string text) =>
        text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("://", StringComparison.Ordinal) ||
        (text.Contains('.') && !text.Contains(' ') && text.Length > 8);

    private static List<PlanLink> Normalize(IEnumerable<Found> raw)
    {
        var links = new List<PlanLink>();
        var seen = new Dictionary<string, PlanLink>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in raw)
        {
            var url = Clean(candidate.Url);
            if (url is null) continue;

            var (kind, videoId) = Classify(url);

            // Rebuild provider URLs from the id we matched rather than trusting the raw text.
            // Extraction can drag a trailing character in, and a video link that 404s is worse
            // than no card at all. This also drops tracking parameters.
            if (videoId is not null)
                url = Canonical(kind, videoId);

            // Dedupe on the video where we recognise one, so the same clip linked as youtu.be
            // and youtube.com/watch collapses into a single card.
            var key = videoId is null ? url : $"{kind}:{videoId}";

            if (seen.TryGetValue(key, out var existing))
            {
                // The same video found twice — once as an annotation, once as text. Keep
                // whichever pass produced real context.
                var merged = Better(existing.Label, candidate.Anchor);
                if (merged is not null && merged != existing.Label)
                {
                    existing.Label = merged;
                    existing.LabelFromDocument = true;
                }
                existing.Section ??= candidate.Section;
                continue;
            }

            var link = new PlanLink
            {
                Url = url,
                Kind = kind,
                VideoId = videoId,
                Label = candidate.Anchor ?? string.Empty,
                LabelFromDocument = !string.IsNullOrWhiteSpace(candidate.Anchor),
                Section = candidate.Section,
                SortOrder = links.Count,
                // Videos lead; everything else is still listed but starts hidden so a link
                // from a document footer doesn't take up a card.
                IsHidden = kind == LinkKind.Other
            };

            seen[key] = link;
            links.Add(link);
        }

        // Recognised videos first, preserving document order within each group.
        var ordered = links
            .OrderBy(l => l.Kind == LinkKind.Other ? 1 : 0)
            .ThenBy(l => l.SortOrder)
            .ToList();

        // Fallbacks only. Anything assigned here is a placeholder, so LabelFromDocument stays
        // false and a looked-up video title may still replace it — see ApplyVideoTitles.
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
            if (string.IsNullOrWhiteSpace(ordered[i].Label))
                ordered[i].Label = ordered[i].Section ?? DefaultLabel(ordered[i].Kind, i + 1);

            // A label identical to its own section heading is noise on a grouped card.
            if (ordered[i].Section is { } section &&
                string.Equals(ordered[i].Label, section, StringComparison.OrdinalIgnoreCase))
            {
                ordered[i].Section = null;
            }
        }

        return ordered;
    }

    /// <summary>
    /// Applies looked-up video titles, and settles what each card is called.
    ///
    /// Naming order, best first:
    ///   1. What the plan calls it — a hyperlink's anchor text, or the line it sits on.
    ///      The coach chose those words for this practice; a channel's own title rarely
    ///      beats "D-to-D reversal".
    ///   2. The published video title — this is what rescues a bare URL on its own line,
    ///      which is the usual cause of "Video 1".
    ///   3. The section heading it sits under.
    ///   4. "Video N".
    ///
    /// Call after PopulateTitlesAsync so the titles are there to choose from.
    /// </summary>
    public static void ApplyVideoTitles(IReadOnlyList<PlanLink> links)
    {
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.VideoTitle)) continue;

            // Replace only a label the document didn't actually provide. A coach's own wording
            // ("D-to-D reversal") beats a channel's title; a placeholder never does.
            if (!link.LabelFromDocument)
                link.Label = link.VideoTitle!;
        }
    }


    /// <summary>Prefers a real description over an empty or numbered placeholder.</summary>
    private static string? Better(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return current;
        if (string.IsNullOrWhiteSpace(current)) return candidate;
        return candidate.Length > current.Length ? candidate : current;
    }

    private static string? Clean(string candidate)
    {
        var url = candidate.Trim();

        // PDF text extraction commonly drags trailing sentence punctuation into the match.
        url = url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');

        if (url.Length is < 12 or > 1000) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;

        return url;
    }

    private static (LinkKind, string?) Classify(string url)
    {
        var yt = YouTubePattern().Match(url);
        if (yt.Success) return (LinkKind.YouTube, yt.Groups[1].Value);

        var vimeo = VimeoPattern().Match(url);
        if (vimeo.Success) return (LinkKind.Vimeo, vimeo.Groups[1].Value);

        return (LinkKind.Other, null);
    }

    private static string Canonical(LinkKind kind, string videoId) => kind switch
    {
        LinkKind.YouTube => $"https://www.youtube.com/watch?v={videoId}",
        LinkKind.Vimeo   => $"https://vimeo.com/{videoId}",
        _                => videoId
    };

    private static string DefaultLabel(LinkKind kind, int position) => kind switch
    {
        LinkKind.YouTube => $"Video {position}",
        LinkKind.Vimeo   => $"Video {position}",
        _                => "Link"
    };

    /// <summary>Provider thumbnail, or null when we don't recognise the host.</summary>
    public static string? ThumbnailUrl(PlanLink link) => link.Kind switch
    {
        LinkKind.YouTube when link.VideoId is not null => $"https://i.ytimg.com/vi/{link.VideoId}/hqdefault.jpg",
        _ => null
    };

    /// <summary>
    /// Embeddable player URL, or null when the link can't be framed and has to open externally.
    ///
    /// playsinline stops iOS hijacking the whole screen with the native player, and
    /// youtube-nocookie avoids setting tracking cookies on a page teenagers are told to visit.
    /// </summary>
    public static string? EmbedUrl(PlanLink link) => link.Kind switch
    {
        LinkKind.YouTube when link.VideoId is not null =>
            $"https://www.youtube-nocookie.com/embed/{link.VideoId}?autoplay=1&rel=0&playsinline=1&modestbranding=1",
        LinkKind.Vimeo when link.VideoId is not null =>
            $"https://player.vimeo.com/video/{link.VideoId}?autoplay=1&playsinline=1",
        _ => null
    };
}
