using HockeyPractice.Models;

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
}

public class TeamSummary
{
    public Team Team { get; init; } = null!;
    public int PlanCount { get; init; }
    public int PlayerCount { get; init; }
}
