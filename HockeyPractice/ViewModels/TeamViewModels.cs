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

    /// <summary>Manager rights as currently rendered — false while previewing as a player.</summary>
    public bool IsManager => Level >= TeamAccessLevel.Manager;

    /// <summary>Manager rights actually held, regardless of preview. Drives navigation.</summary>
    public bool IsRealManager => RealLevel >= TeamAccessLevel.Manager;

    /// <summary>True when a manager is deliberately looking at the player's view.</summary>
    public bool PreviewingAsPlayer => IsRealManager && Level < TeamAccessLevel.Manager;

    /// <summary>Access here was taken through the admin panel, not the team's own code.</summary>
    public bool ViaSiteAdmin { get; init; }

    /// <summary>Viewer answered the who-are-you question with "I am a non-player".</summary>
    public bool IsParent { get; init; }

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

    /// <summary>
    /// Asking for the manager code rather than the team code. Only changes what the page says
    /// and where it sends you — either code is still accepted, and each grants exactly what it
    /// is, so entering the wrong one on the wrong screen can't escalate anything.
    /// </summary>
    public bool ManageMode { get; init; }
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

    /// <summary>
    /// Whether a real mail provider is configured. When it isn't, the signup box is not shown
    /// at all — offering it and then never sending the confirmation email is a trap, not a
    /// feature, and that was exactly the live state before this flag was consulted.
    /// </summary>
    public bool EmailSignupAvailable { get; init; }
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

/// <summary>The landing page: pick a team, then choose how you're going in.</summary>
public class HomeViewModel
{
    public List<Team> Teams { get; init; } = new();

    /// <summary>Access this device already holds per team, so returning users skip the code.</summary>
    public Dictionary<int, TeamAccessLevel> Access { get; init; } = new();

    public TeamAccessLevel LevelFor(int teamId) =>
        Access.TryGetValue(teamId, out var level) ? level : TeamAccessLevel.None;
}
