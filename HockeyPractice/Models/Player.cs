using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// A roster entry. Deliberately minimal: a name and a number, nothing more.
/// This is a roster of minors — no photos, birthdates, addresses or phone numbers.
/// </summary>
public class Player
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(4)]
    public string? JerseyNumber { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
