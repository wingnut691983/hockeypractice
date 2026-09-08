using HockeyPractice.Models;
using Microsoft.EntityFrameworkCore;

namespace HockeyPractice.Infrastructure;

/// <summary>
/// The drill and plan search predicates, in one place.
///
/// The drill library and the plan editor's picker run near-identical queries in two different
/// controllers. Holding the filters here is what stops them drifting apart the next time one of
/// them changes.
/// </summary>
public static class DrillSearch
{
    /// <summary>
    /// Drills carrying a tag that contains the given text, or every drill when nothing is given.
    ///
    /// Matches on NormalizedName, which is stored pre-lowercased precisely because EF Core's
    /// SQLite provider translates string.Contains() to instr(), and instr() is case-sensitive.
    /// </summary>
    public static IQueryable<Drill> MatchingTag(this IQueryable<Drill> drills, string? tag)
    {
        var needle = tag?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(needle)) return drills;

        return drills.Where(d => d.Tags.Any(t => t.NormalizedName.Contains(needle)));
    }

    /// <summary>
    /// Drills whose title contains the given text, or every drill when nothing is given.
    ///
    /// LIKE rather than Contains, and this is the whole reason the method exists: Contains becomes
    /// instr(), which is case-sensitive, so typing "breakout" would fail to find "Breakout Reps".
    /// SQLite's LIKE folds ASCII case by default, so this matches without needing a lowercased
    /// copy of every title kept in sync on save, on edit and on copy between teams. There is no
    /// index to lose either way: a leading-wildcard match cannot use one.
    /// </summary>
    public static IQueryable<Drill> MatchingName(this IQueryable<Drill> drills, string? name)
    {
        var needle = name?.Trim();
        if (string.IsNullOrEmpty(needle)) return drills;

        var pattern = $"%{LikeEscape(needle)}%";
        return drills.Where(d => EF.Functions.Like(d.Title, pattern, LikeEscapeChar));
    }

    /// <summary>Plans whose title contains the given text. Same reasoning as MatchingName.</summary>
    public static IQueryable<PracticePlan> MatchingTitle(this IQueryable<PracticePlan> plans, string? name)
    {
        var needle = name?.Trim();
        if (string.IsNullOrEmpty(needle)) return plans;

        var pattern = $"%{LikeEscape(needle)}%";
        return plans.Where(p => EF.Functions.Like(p.Title, pattern, LikeEscapeChar));
    }

    /// <summary>
    /// One page of results, plus what the pager needs to describe itself. <see cref="Page"/> is
    /// the page actually served, which is not always the one asked for.
    /// </summary>
    public record PagedResult<T>(List<T> Items, int Page, int TotalPages, int TotalItems)
    {
        public static PagedResult<T> Empty => new(new List<T>(), 1, 0, 0);
    }

    /// <summary>
    /// Counts, clamps and slices, in that order.
    ///
    /// The clamp is the part that matters: a bookmarked ?page=99, or a page that stopped existing
    /// because drills were deleted or a filter narrowed the list, lands on the last real page
    /// instead of rendering an empty list under a pager that says there are results. Callers pass
    /// the page straight from the query string and do not validate it.
    ///
    /// The query must already be ordered, and ordered by something unique enough to be a total
    /// order. Skip/Take over an ambiguous sort can serve the same row on two pages and never serve
    /// another.
    /// </summary>
    public static async Task<PagedResult<T>> ToPageAsync<T>(this IQueryable<T> query, int page, int size)
    {
        var total = await query.CountAsync();
        if (total == 0) return PagedResult<T>.Empty;

        var totalPages = (int)Math.Ceiling(total / (double)size);
        var current = Math.Clamp(page, 1, totalPages);

        var items = await query.Skip((current - 1) * size).Take(size).ToListAsync();
        return new PagedResult<T>(items, current, totalPages, total);
    }

    public const string LikeEscapeChar = "\\";

    /// <summary>
    /// Neutralises the LIKE wildcards. Without this a coach typing "%" would match every drill and
    /// "_" would match any single character, which reads as the search being broken rather than as
    /// a pattern being honoured. The escape character itself has to go first.
    /// </summary>
    public static string LikeEscape(string value) => value
        .Replace(LikeEscapeChar, LikeEscapeChar + LikeEscapeChar)
        .Replace("%", LikeEscapeChar + "%")
        .Replace("_", LikeEscapeChar + "_");
}
