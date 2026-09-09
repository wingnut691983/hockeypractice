using HockeyPractice.Models;
using HockeyPractice.Services;

namespace HockeyPractice.ViewModels;

public class AdminViewModel
{
    public List<TeamSummary> Teams { get; init; } = new();
    public string? Error { get; init; }
    public string? Notice { get; init; }
    public long UsedBytes { get; init; }
    public long QuotaBytes { get; init; }
    public bool Configured { get; init; }

    /// <summary>Whether the site is refusing writes right now, and for how much longer.</summary>
    public bool WritesPaused { get; init; }
    public int PauseMinutesLeft { get; init; }

    public long DatabaseBytes { get; init; }

    /// <summary>The database a restore moved aside, if one ever has. Shown because it is the
    /// only way back from restoring the wrong file.</summary>
    public long ReplacedBytes { get; init; }
    public DateTime? ReplacedAtUtc { get; init; }
    public string ReplacedPath { get; init; } = "";

    // ── Off-site backups ─────────────────────────────────────────────────

    /// <summary>False when no bucket is configured, which is the normal state locally.</summary>
    public bool ArchivingConfigured { get; init; }

    /// <summary>
    /// What is actually in the bucket, newest first. This is where "when did it last run" comes
    /// from: a singleton would forget on every restart, and a restore restarts on purpose.
    /// </summary>
    public IReadOnlyList<StoredBackup> Archives { get; init; } = Array.Empty<StoredBackup>();

    /// <summary>Set when the bucket could not be listed, so the page says so instead of
    /// silently showing an empty list that looks like "no backups".</summary>
    public string? ArchiveListError { get; init; }

    /// <summary>The last failure in THIS process, which is the only thing memory can tell us.</summary>
    public DateTime? ArchiveLastAttemptUtc { get; init; }
    public bool ArchiveLastAttemptFailed { get; init; }
    public string? ArchiveError { get; init; }
    public bool ArchiveRunning { get; init; }
    public int ArchiveKeep { get; init; }
    public int ArchiveHourUtc { get; init; }
}

public class TeamSummary
{
    public Team Team { get; init; } = null!;
    public int PlanCount { get; init; }
    public int PlayerCount { get; init; }
}
