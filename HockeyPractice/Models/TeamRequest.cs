using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>Where a request sits. New ones are what the admin page counts as waiting.</summary>
public enum TeamRequestStatus
{
    New = 0,
    Handled = 1
}

/// <summary>
/// Somebody asking to have a team set up for them, from the public form on the home page.
///
/// Deliberately not a team, and not an account. Creating a team is a site-admin action with codes
/// to issue and a slug to choose, so this is a message with enough on it to act: who asked, how to
/// reach them, and what they want called what. The admin reads it, creates the team by hand the
/// way they always have, and marks it handled.
///
/// Every field here is typed by an anonymous stranger, so nothing on it is trusted: lengths are
/// enforced in the controller rather than left to the <c>MaxLength</c> attributes, which SQLite
/// does not enforce, and every value is HTML-escaped by Razor wherever it is shown.
/// </summary>
public class TeamRequest
{
    public int Id { get; set; }

    [Required, MaxLength(80)] public string FirstName { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string LastName { get; set; } = string.Empty;

    /// <summary>How to reach them. The only field the admin actually has to act on.</summary>
    [Required, MaxLength(200), EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>The club or association, e.g. "Elgin Blades".</summary>
    [Required, MaxLength(140)] public string Association { get; set; } = string.Empty;

    /// <summary>What they want the team called, e.g. "2012 AA".</summary>
    [Required, MaxLength(140)] public string TeamName { get; set; } = string.Empty;

    public TeamRequestStatus Status { get; set; } = TeamRequestStatus.New;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the admin marked it dealt with. Null while it is still waiting.</summary>
    public DateTime? HandledUtc { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
