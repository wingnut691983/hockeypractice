namespace HockeyPractice.Models;

/// <summary>
/// What someone can do within one team. Computed per request, never persisted.
///
/// Deliberately does NOT include site admin. Running the site and running a team are separate
/// jobs: whoever creates teams and holds the codes is not automatically the person who uploads
/// practice plans, and folding them into one ladder made every site admin an implicit manager
/// of every team. Site admin is a site-wide capability, checked on its own.
/// </summary>
public enum TeamAccessLevel
{
    None = 0,

    /// <summary>A player or parent: read the plans. What almost everyone is.</summary>
    Player = 1,

    /// <summary>
    /// Coach or team manager. An elevated Player — everything a player can do, plus uploading
    /// and editing plans, the roster, and team branding.
    /// </summary>
    Manager = 2
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
