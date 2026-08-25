namespace HockeyPractice.Models;

/// <summary>Effective access for a viewer against one team. Computed per request, never persisted.</summary>
public enum TeamAccessLevel
{
    None      = 0,
    Viewer    = 1,
    Coach     = 2,
    SiteAdmin = 3
}

/// <summary>A plan is invisible to players and sends no email until it is Published.</summary>
public enum PlanStatus
{
    Draft     = 0,
    Published = 1
}

/// <summary>Persisted as int — do not renumber.</summary>
public enum LinkKind
{
    Other   = 0,
    YouTube = 1,
    Vimeo   = 2
}
