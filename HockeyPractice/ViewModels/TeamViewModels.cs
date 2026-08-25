using HockeyPractice.Models;

namespace HockeyPractice.ViewModels;

/// <summary>Branding + viewer identity shared by every team-scoped page.</summary>
public class TeamContext
{
    public Team Team { get; init; } = null!;

    /// <summary>Level to render with — clamped to Viewer while a coach previews player view.</summary>
    public TeamAccessLevel Level { get; init; }

    /// <summary>Level actually held. Drives navigation that must stay reachable in preview.</summary>
    public TeamAccessLevel RealLevel { get; init; }

    public Player? Me { get; init; }
    public string? LogoUrl { get; init; }

    public bool IsCoach => Level >= TeamAccessLevel.Coach;
    public bool IsSiteAdmin => RealLevel >= TeamAccessLevel.SiteAdmin;

    /// <summary>True when a coach is deliberately looking at the player's view.</summary>
    public bool PreviewingAsPlayer => RealLevel >= TeamAccessLevel.Coach && Level < TeamAccessLevel.Coach;

    /// <summary>Other teams this device can reach — drives the switcher once there's more than one.</summary>
    public List<TeamLink> OtherTeams { get; init; } = new();
}

public record TeamLink(string Slug, string Name);

public class EnterCodeViewModel
{
    public Team Team { get; init; } = null!;
    public string? Error { get; init; }
    public string? ReturnUrl { get; init; }
    public string? LogoUrl { get; init; }
}

public class RosterPickViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public List<Player> Players { get; init; } = new();
    public string? ReturnUrl { get; init; }
}

public class PlanListViewModel
{
    public TeamContext Ctx { get; init; } = null!;

    /// <summary>The soonest practice that has not finished yet, if any.</summary>
    public PlanCard? Next { get; init; }
    public List<PlanCard> Upcoming { get; init; } = new();
    public List<PlanCard> Past { get; init; } = new();
    public bool HasAny => Next != null || Upcoming.Count > 0 || Past.Count > 0;
}

public class PlanCard
{
    public PracticePlan Plan { get; init; } = null!;
    public int VideoCount { get; init; }
    public bool ViewedByMe { get; init; }

    /// <summary>"Tomorrow, 6:15 PM" for anything close, otherwise an absolute date.</summary>
    public string WhenLabel { get; init; } = string.Empty;
    public bool IsDraft => Plan.Status == PlanStatus.Draft;
}

public class PlanDetailViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public PracticePlan Plan { get; init; } = null!;
    public List<PlanLink> Videos { get; init; } = new();
    public string WhenLabel { get; init; } = string.Empty;
    public bool ViewedByMe { get; init; }

    /// <summary>Coach-only roster tracking. Empty for players and parents.</summary>
    public List<Player> Viewed { get; init; } = new();
    public List<Player> NotViewed { get; init; } = new();
    public int AnonymousViews { get; init; }
}
