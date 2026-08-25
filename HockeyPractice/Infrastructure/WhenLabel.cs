namespace HockeyPractice.Infrastructure;

/// <summary>
/// Renders a practice time the way a player reads it. "Tomorrow, 6:15 PM" tells a 15-year-old
/// whether they need to watch the videos tonight; "Feb 14, 2026" makes them do date arithmetic.
/// </summary>
public static class WhenLabel
{
    public static string For(DateTime practiceLocal, string timeZoneId)
    {
        var today = NowIn(timeZoneId).Date;
        var days = (practiceLocal.Date - today).Days;
        var time = practiceLocal.ToString("h:mm tt");

        return days switch
        {
            0  => $"Today, {time}",
            1  => $"Tomorrow, {time}",
            -1 => $"Yesterday, {time}",
            > 1 and < 7  => $"{practiceLocal:dddd}, {time}",
            < -1 and > -7 => $"Last {practiceLocal:dddd}, {time}",
            _ => practiceLocal.Year == today.Year
                    ? $"{practiceLocal:ddd MMM d}, {time}"
                    : $"{practiceLocal:MMM d yyyy}, {time}"
        };
    }

    public static DateTime NowIn(string timeZoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad IANA id shouldn't take a page down — the label just loses its local nuance.
            return DateTime.UtcNow;
        }
    }
}
