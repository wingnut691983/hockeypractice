using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

public class Team
{
    public int Id { get; set; }

    /// <summary>URL segment, e.g. "bantam-a". Unique across the site.</summary>
    [Required, MaxLength(60)]
    public string Slug { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Logo filename relative to the team directory, or null when none uploaded.</summary>
    [MaxLength(120)]
    public string? LogoFileName { get; set; }

    [MaxLength(9)] public string PrimaryColor { get; set; } = "#0B4EA2";
    [MaxLength(9)] public string AccentColor  { get; set; } = "#F0562D";

    /// <summary>Shared with players and parents. Rotatable without a redeploy.</summary>
    [Required] public string ViewCodeHash { get; set; } = string.Empty;

    /// <summary>Coach tier: upload, publish, roster, branding, view tracking.</summary>
    [Required] public string CoachCodeHash { get; set; } = string.Empty;

    /// <summary>IANA id, e.g. "America/Chicago". Used only to render "today"/"tomorrow" correctly.</summary>
    [MaxLength(60)] public string TimeZoneId { get; set; } = "America/Chicago";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<Player> Players { get; set; } = new();
    public List<PracticePlan> Plans { get; set; } = new();
    public List<Subscriber> Subscribers { get; set; } = new();
}
