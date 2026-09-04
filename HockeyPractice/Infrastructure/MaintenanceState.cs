namespace HockeyPractice.Infrastructure;

/// <summary>
/// Whether the site is currently refusing writes, so a backup can be taken or restored without
/// a coach saving a plan halfway through it.
///
/// Held in memory on purpose, and with a deadline. A pause is a thing one person turns on for a
/// couple of minutes, and the failure that actually bites is not "the pause was lost" but "the
/// pause was forgotten": a site stuck read-only, a coach unable to post Thursday's plan, and
/// nothing on screen explaining why. Both the deadline and a restart clear it, so the site can
/// only ever get stuck open, never stuck shut.
///
/// One consequence worth knowing: this is per process. It is the right shape for a single
/// instance, which is what UpTurtle runs; a second replica would need this moved to the volume.
/// </summary>
public class MaintenanceState
{
    /// <summary>How long a pause lasts before it lifts itself. Generous next to the seconds a
    /// download takes, short enough that a forgotten pause is over before anyone needs to write.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    /// <summary>When the pause lifts on its own, or null when writes are allowed.</summary>
    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (_gate)
                return _until > DateTimeOffset.UtcNow ? _until : null;
        }
    }

    public bool IsPaused => PausedUntil is not null;

    /// <summary>Whole minutes left, rounded up, for telling someone how long to wait.</summary>
    public int MinutesLeft
    {
        get
        {
            if (PausedUntil is not { } until) return 0;
            var left = until - DateTimeOffset.UtcNow;
            return left <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(left.TotalMinutes);
        }
    }

    /// <summary>Starts a pause, or restarts the clock on one already running.</summary>
    public void Pause()
    {
        lock (_gate) _until = DateTimeOffset.UtcNow + Window;
    }

    public void Resume()
    {
        lock (_gate) _until = DateTimeOffset.MinValue;
    }
}
