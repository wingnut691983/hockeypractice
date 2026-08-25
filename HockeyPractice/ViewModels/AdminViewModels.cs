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
}

public class TeamSummary
{
    public Team Team { get; init; } = null!;
    public int PlanCount { get; init; }
    public int PlayerCount { get; init; }
}
