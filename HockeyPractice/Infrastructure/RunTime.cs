namespace HockeyPractice.Infrastructure;

/// <summary>
/// Renders drill and practice lengths the way a coach says them out loud: "12 min",
/// "1 hr 25 min". Sixty-plus minutes as a bare minute count ("85 min") makes you do the
/// arithmetic yourself, which is the one moment you actually care — is this longer than the ice
/// time I booked?
/// </summary>
public static class RunTime
{
    /// <summary>Longest run time accepted for one drill. Anything above this is a typo.</summary>
    public const int MaxMinutes = 240;

    public static string Human(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} hr" : $"{hours} hr {rest} min";
    }

    /// <summary>
    /// A practice total, plus an honest note when some drills have no estimate yet. Reporting a
    /// bare sum over a partially-filled plan would read as the whole practice while quietly
    /// leaving drills out.
    /// </summary>
    public static string PlanTotal(int totalMinutes, int missingCount)
    {
        if (totalMinutes == 0)
        {
            return missingCount > 0
                ? "No run times set yet"
                : "";
        }

        var label = Human(totalMinutes);
        if (missingCount == 0) return label;

        return $"{label} + {missingCount} drill{(missingCount == 1 ? "" : "s")} with no time set";
    }
}
