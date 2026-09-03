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

    /// <summary>
    /// Length of a practice, used to work out what the clock reads when each drill starts.
    /// A single number because both teams book the same slot; if that stops being true it wants
    /// to move onto Team rather than be threaded through as a parameter.
    /// </summary>
    public const int PracticeMinutes = 80;

    /// <summary>
    /// Minutes as a countdown clock — "80:00", "65:00". Negative stays signed ("-5:00"), which is
    /// the useful answer rather than an error: it says by how much the plan overruns the ice time
    /// and therefore how much has to come out.
    /// </summary>
    public static string Clock(int minutes)
    {
        var sign = minutes < 0 ? "-" : "";
        return $"{sign}{Math.Abs(minutes)}:00";
    }

    /// <summary>
    /// What the clock reads as each drill begins, counting down from a full practice.
    ///
    /// Returns null for a drill whose start can't be known — which happens from the first drill
    /// with no duration onward, because nothing after an unknown length can be placed. The drill
    /// with the missing time still gets its own start, since that much IS known. Guessing (by
    /// treating a blank as zero) would print confident times that are quietly wrong.
    /// </summary>
    public static List<int?> StartTimes(IEnumerable<int?> durations, int practiceMinutes = PracticeMinutes)
    {
        var starts = new List<int?>();
        var remaining = practiceMinutes;
        var known = true;

        foreach (var duration in durations)
        {
            starts.Add(known ? remaining : null);

            if (duration is int minutes) remaining -= minutes;
            else known = false;
        }

        return starts;
    }

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
