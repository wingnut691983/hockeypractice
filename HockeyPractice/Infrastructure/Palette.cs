using System.Globalization;
using System.Text.RegularExpressions;

namespace HockeyPractice.Infrastructure;

/// <summary>
/// Derives readable text colours from a team's chosen colours.
///
/// Teams pick their real colours, and plenty of real hockey colours are light — gold, powder
/// blue, white. Hard-coding white text on top of them produced button labels that couldn't be
/// read. Everything here is WCAG relative luminance and contrast ratio, computed once per
/// request and handed to CSS as custom properties, because CSS has no portable way to ask
/// "what text colour works on this background?".
/// </summary>
public static class Palette
{
    // WCAG AA for normal-size text. Large text would allow 3.0, but these labels are small.
    private const double MinContrast = 4.5;

    private const string Ink   = "#10141a";   // near-black, matches --hp-text
    private const string Paper = "#ffffff";

    // Surfaces the accent may be drawn on as text.
    private const string LightSurface = "#ffffff";
    private const string DarkSurface  = "#171c24";

    /// <summary>Black or white — whichever is legible on this background.</summary>
    public static string On(string background)
    {
        var bg = Parse(background);
        return Contrast(bg, Parse(Ink)) >= Contrast(bg, Parse(Paper)) ? Ink : Paper;
    }

    /// <summary>
    /// Text colour for a gradient running between two colours. Picks whichever of black or
    /// white keeps the *worst* end readable — text spans the whole sweep, so the weaker end
    /// is what decides it.
    /// </summary>
    public static string OnGradient(string from, string to)
    {
        var a = Parse(from);
        var b = Parse(to);

        var ink = Math.Min(Contrast(a, Parse(Ink)), Contrast(b, Parse(Ink)));
        var paper = Math.Min(Contrast(a, Parse(Paper)), Contrast(b, Parse(Paper)));
        return ink >= paper ? Ink : Paper;
    }

    /// <summary>
    /// The colour adjusted until it is readable as text on the given surface. A team's gold
    /// stays recognisably gold, just darkened enough to be legible on white — rather than
    /// being replaced by a generic colour that loses the team's identity entirely.
    /// </summary>
    public static string AsTextOn(string colour, bool darkSurface)
    {
        var surface = Parse(darkSurface ? DarkSurface : LightSurface);
        var (h, s, l) = ToHsl(Parse(colour));

        // Walk lightness toward the readable side in small steps, keeping hue and saturation.
        for (var i = 0; i <= 100; i++)
        {
            var candidate = FromHsl(h, s, Math.Clamp(darkSurface ? l + i * 0.01 : l - i * 0.01, 0, 1));
            if (Contrast(surface, candidate) >= MinContrast)
                return Hex(candidate);
        }

        return darkSurface ? Paper : Ink;
    }

    /// <summary>Blends two colours, matching what CSS color-mix does in sRGB.</summary>
    public static string Mix(string a, double weightA, string b)
    {
        var x = Parse(a);
        var y = Parse(b);
        var w = Math.Clamp(weightA, 0, 1);
        return Hex((x.R * w + y.R * (1 - w),
                    x.G * w + y.G * (1 - w),
                    x.B * w + y.B * (1 - w)));
    }

    /// <summary>
    /// The full set of custom properties a team's colours imply, ready to drop into a style
    /// attribute. Emitted per team rather than per page so the landing page can theme each
    /// card independently.
    /// </summary>
    public static string CssVariables(string primary, string accent)
    {
        // Matches the "next practice" card's gradient end in the stylesheet.
        var gradientEnd = Mix(primary, 0.62, accent);

        return string.Join(" ",
            $"--hp-primary:{primary};",
            $"--hp-accent:{accent};",
            $"--hp-on-primary:{On(primary)};",
            $"--hp-on-accent:{On(accent)};",
            $"--hp-next-to:{gradientEnd};",
            $"--hp-on-next:{OnGradient(primary, gradientEnd)};",
            $"--hp-primary-ink:{AsTextOn(primary, false)};",
            $"--hp-primary-ink-dark:{AsTextOn(primary, true)};",
            $"--hp-accent-ink:{AsTextOn(accent, false)};",
            $"--hp-accent-ink-dark:{AsTextOn(accent, true)};");
    }

    // ── WCAG maths ───────────────────────────────────────────────────────

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance((double R, double G, double B) c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(double v) =>
        v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    // ── conversions ──────────────────────────────────────────────────────

    private static (double R, double G, double B) Parse(string hex)
    {
        if (!Regex.IsMatch(hex ?? "", "^#[0-9a-fA-F]{6}$"))
            return (0, 0, 0);

        return (
            int.Parse(hex!.Substring(1, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber) / 255.0,
            int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber) / 255.0);
    }

    private static string Hex((double R, double G, double B) c) =>
        $"#{Byte(c.R):x2}{Byte(c.G):x2}{Byte(c.B):x2}";

    private static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static (double H, double S, double L) ToHsl((double R, double G, double B) c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        var l = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-9) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - c.R) < 1e-9) h = (c.G - c.B) / d + (c.G < c.B ? 6 : 0);
        else if (Math.Abs(max - c.G) < 1e-9) h = (c.B - c.R) / d + 2;
        else h = (c.R - c.G) / d + 4;

        return (h / 6, s, l);
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s < 1e-9) return (l, l, l);

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return (Hue(p, q, h + 1.0 / 3), Hue(p, q, h), Hue(p, q, h - 1.0 / 3));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
