namespace HockeyPractice.Infrastructure;

/// <summary>
/// The one place that decides what a link preview says about a plan.
///
/// A coach shares a plan into the team chat and the chat app fetches the URL to build the card.
/// It fetches it with no cookies, so it is refused by the team-code gate and lands on the code
/// page; before this existed, every shared plan previewed as the team's name because that is
/// whatever title the crawler happened to end up on.
///
/// There is no way to tell a chat app the plan's name and withhold it from a person: the crawler
/// is an ordinary HTTP client, so anything the card can show, anyone holding the link can read.
/// That is why what goes in here was a decision and not a detail. It is the plan's TITLE and the
/// team's name, and nothing else. No date, no location, no drills, no roster, and published plans
/// only. Widening it is a privacy change, not a copy tweak, so change it here and nowhere else:
/// both the code page and the plan's own page render from these two methods so the two can never
/// drift apart.
/// </summary>
public static class PlanPreview
{
    /// <summary>The card's headline. The plan's own title, alone, by deliberate choice.</summary>
    public static string Title(string planTitle) =>
        string.IsNullOrWhiteSpace(planTitle) ? "Practice plan" : planTitle.Trim();

    /// <summary>
    /// The card's second line. Names the team, which is already public on the home page, and says
    /// plainly that the card is not the plan: someone who taps it still needs the code.
    /// </summary>
    public static string Description(string teamName) =>
        $"Practice plan for {teamName}. The team code is needed to read it.";

    /// <summary>
    /// Pulls a plan id out of a captured returnUrl, or null when the value does not name exactly
    /// one plan page belonging to <paramref name="slug"/>.
    ///
    /// This value arrives in a query string from anywhere, so it is matched rather than trusted,
    /// the same way <c>TeamScopedController.TryReturnUrlRedirect</c> refuses anything that is not
    /// a plain rooted local path. Two constraints here are what keep the exposure to what was
    /// agreed, and both are load-bearing:
    ///
    /// The match is anchored on the WHOLE path and on exactly five segments, so the only thing
    /// this can ever name is a plan's own page. A trailing <c>/print</c> or <c>/overview</c>, an
    /// extra segment, or an encoded separator all fail it rather than resolving to something
    /// adjacent.
    ///
    /// The slug in the path must equal the team whose code page is being rendered. Without that,
    /// one team's code page would happily caption another team's plan, and the returnUrl is
    /// attacker-supplied, so it would be a free cross-team read of plan titles.
    ///
    /// The caller still has to check the plan is published and belongs to the team; this only
    /// parses. See <c>TeamController.EnterCode</c>.
    /// </summary>
    public static int? PlanIdFromReturnUrl(string? returnUrl, string? basePath, string slug)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || string.IsNullOrWhiteSpace(slug)) return null;

        var target = returnUrl!;

        // Rejected before anything else, exactly as TryReturnUrlRedirect does it: "//host" and
        // any absolute URL are out, so nothing off-site can reach the segment match below.
        if (!target.StartsWith('/') || target.StartsWith("//") || target.Contains(':')) return null;

        // The share button captures window.location.href, which can carry a query or a fragment.
        // Neither is part of the path being matched.
        var cut = target.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) target = target[..cut];

        // The prefix may or may not be present: CurrentPathForReturn includes PathBase, and a link
        // written by hand or issued before that was fixed does not. Strip it when it is there
        // rather than failing the match over it.
        basePath ??= string.Empty;
        if (basePath.Length > 0 &&
            target.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            target = target[basePath.Length..];
        }

        // "" / "t" / <slug> / "plans" / <id>
        var parts = target.Split('/');
        if (parts.Length != 5) return null;
        if (parts[0].Length != 0) return null;
        if (!string.Equals(parts[1], "t", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(parts[2], slug, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(parts[3], "plans", StringComparison.OrdinalIgnoreCase)) return null;

        return int.TryParse(parts[4], out var id) && id > 0 ? id : null;
    }
}
