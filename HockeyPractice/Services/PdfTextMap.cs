using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace HockeyPractice.Services;

/// <summary>
/// A page's text reassembled into lines with their geometry, so a link can be described by
/// what is written around it rather than by a counter.
///
/// PDF has no notion of a paragraph or a heading — it is glyphs at coordinates. Everything
/// here is inference from position and size, so it is deliberately conservative: when a guess
/// would be worse than nothing, it returns nothing and the caller falls back.
/// </summary>
public sealed partial class PdfTextMap
{
    public sealed record Line(string Text, double Top, double Bottom, double Left, double FontSize)
    {
        public bool VerticallyOverlaps(double top, double bottom) =>
            Bottom < top && Top > bottom;
    }

    private readonly List<Line> _lines;
    private readonly HashSet<int> _headingIndexes;

    private PdfTextMap(List<Line> lines, HashSet<int> headingIndexes)
    {
        _lines = lines;
        _headingIndexes = headingIndexes;
    }

    public IReadOnlyList<Line> Lines => _lines;

    [GeneratedRegex(@"^\s*(\d+[\.\)]|[A-Z][\.\)]|[IVXLC]+[\.\)])\s+\S")]
    private static partial Regex NumberedItem();

    public static PdfTextMap Build(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

        // Group words into lines by vertical overlap. Tolerance scales with glyph height so
        // a 20pt heading and 9pt footnote both cluster correctly.
        var lines = new List<Line>();
        foreach (var group in words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.0))
            .OrderByDescending(g => g.Max(w => w.BoundingBox.Top)))
        {
            var ordered = group.OrderBy(w => w.BoundingBox.Left).ToList();
            if (ordered.Count == 0) continue;

            var text = string.Join(" ", ordered.Select(w => w.Text)).Trim();
            if (text.Length == 0) continue;

            var sizes = ordered.SelectMany(w => w.Letters).Select(l => l.PointSize).ToList();

            lines.Add(new Line(
                Text: text,
                Top: ordered.Max(w => w.BoundingBox.Top),
                Bottom: ordered.Min(w => w.BoundingBox.Bottom),
                Left: ordered.Min(w => w.BoundingBox.Left),
                FontSize: sizes.Count > 0 ? Median(sizes) : 0));
        }

        return new PdfTextMap(lines, FindHeadings(lines));
    }

    /// <summary>
    /// Which lines read as section headings. Body size is taken as the median across the page,
    /// so a plan that is entirely 14pt doesn't turn every line into a heading.
    /// </summary>
    private static HashSet<int> FindHeadings(List<Line> lines)
    {
        var headings = new HashSet<int>();
        if (lines.Count == 0) return headings;

        var bodySize = Median(lines.Select(l => l.FontSize).Where(s => s > 0).ToList());

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var words = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            // A heading is short. A long line is prose no matter how it's styled.
            if (words > 12 || line.Text.Length > 90) continue;

            var bigger = bodySize > 0 && line.FontSize > bodySize * 1.12;
            var numbered = NumberedItem().IsMatch(line.Text);
            var endsWithColon = line.Text.EndsWith(':') && words <= 8;
            var shouty = line.Text.Length <= 45 &&
                         line.Text.Any(char.IsLetter) &&
                         line.Text.Where(char.IsLetter).All(char.IsUpper);

            if (bigger || numbered || endsWithColon || shouty)
                headings.Add(i);
        }

        return headings;
    }

    /// <summary>The line containing a point, or the one a rectangle sits on.</summary>
    public int IndexOfLineAt(double top, double bottom)
    {
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i].VerticallyOverlaps(top, bottom))
                return i;
        return -1;
    }

    public int IndexOfLineContaining(string fragment)
    {
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i].Text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // A heading this far above a link is not describing it. Without this, footer boilerplate
    // at the bottom of the page gets attributed to whatever the last section happened to be.
    private const double MaxHeadingDistancePoints = 220;

    /// <summary>Nearest heading above the given line — the block this link belongs to.</summary>
    public string? SectionFor(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lines.Count) return null;

        var linkTop = _lines[lineIndex].Top;

        for (var i = lineIndex; i >= 0; i--)
        {
            if (!_headingIndexes.Contains(i)) continue;

            // PDF origin is bottom-left, so a heading above the link has the larger Y.
            if (_lines[i].Bottom - linkTop > MaxHeadingDistancePoints) return null;

            return Tidy(_lines[i].Text);
        }

        return null;
    }

    public string? TextOfLine(int lineIndex) =>
        lineIndex >= 0 && lineIndex < _lines.Count ? _lines[lineIndex].Text : null;

    /// <summary>Words whose boxes fall inside a link annotation — its visible anchor text.</summary>
    public string? TextWithin(PdfRectangle rect, Page page)
    {
        var inside = page.GetWords()
            .Where(w =>
                w.BoundingBox.Left >= rect.Left - 1 &&
                w.BoundingBox.Right <= rect.Right + 1 &&
                w.BoundingBox.Bottom >= rect.Bottom - 2 &&
                w.BoundingBox.Top <= rect.Top + 2)
            .OrderBy(w => w.BoundingBox.Left)
            .Select(w => w.Text);

        var text = string.Join(" ", inside).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Trims a raw line into something that reads as a label: drops list numbering, trailing
    /// punctuation, and the lead-in words a coach writes before a link.
    /// </summary>
    public static string? Tidy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = Regex.Replace(raw, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"^\s*(\d+[\.\)]|[A-Z][\.\)]|[IVXLC]+[\.\)])\s+", "");
        text = Regex.Replace(text, @"^(?:watch|see|video|link|reference|ref)\b[:\-–—\s]*", "",
                             RegexOptions.IgnoreCase);
        text = text.Trim(' ', ':', '-', '–', '—', '·', '•', '•', ',', ';', '.');

        if (text.Length < 3) return null;
        return text.Length > 140 ? text[..140].TrimEnd() : text;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
