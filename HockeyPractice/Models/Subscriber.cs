using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// Opt-in email notification when a plan is published. Double opt-in: a row is created on
/// request but only mailed once ConfirmedUtc is set. The coach never bulk-uploads addresses.
/// </summary>
public class Subscriber
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(200), EmailAddress]
    public string Email { get; set; } = string.Empty;

    public int? PlayerId { get; set; }
    public Player? Player { get; set; }

    [Required, MaxLength(64)] public string ConfirmToken { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string UnsubToken   { get; set; } = string.Empty;

    public DateTime? ConfirmedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
