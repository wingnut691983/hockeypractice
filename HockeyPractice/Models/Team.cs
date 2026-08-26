using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

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

    public const string DefaultPrimary = "#0B4EA2";
    public const string DefaultAccent  = "#F0562D";

    /// <summary>
    /// Colours as they are safe to emit into a style attribute. They are validated on save, but
    /// these are interpolated straight into CSS, so anything that isn't a plain hex triple is
    /// replaced rather than trusted — a stray value should lose the team its colour, not let it
    /// write arbitrary CSS onto the page.
    /// </summary>
    [NotMapped] public string SafePrimary => SafeColor(PrimaryColor, DefaultPrimary);
    [NotMapped] public string SafeAccent  => SafeColor(AccentColor,  DefaultAccent);

    private static string SafeColor(string? value, string fallback) =>
        value is not null && Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : fallback;

    /// <summary>Shared with players and parents. Rotatable without a redeploy.</summary>
    [Required] public string ViewCodeHash { get; set; } = string.Empty;

    /// <summary>
    /// The player code in plaintext, so the coach can share the join link all season.
    ///
    /// Deliberate: this code is a shared, low-privilege secret handed to every family on the
    /// team — its entire purpose is to be given out. Storing only its hash made the invite
    /// link unusable the moment the creation notice scrolled away, which gutted the primary
    /// onboarding flow. The manager and site-admin codes guard real capability and stay
    /// hash-only. Null on teams created before this existed, until the code is next rotated.
    /// </summary>
    [MaxLength(12)] public string? ViewCode { get; set; }

    /// <summary>Coach tier: upload, publish, roster, branding, view tracking.</summary>
    [Required] public string CoachCodeHash { get; set; } = string.Empty;

    /// <summary>IANA id, e.g. "America/Chicago". Used only to render "today"/"tomorrow" correctly.</summary>
    [MaxLength(60)] public string TimeZoneId { get; set; } = "America/Chicago";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<Player> Players { get; set; } = new();
    public List<PracticePlan> Plans { get; set; } = new();
    public List<Subscriber> Subscribers { get; set; } = new();
}
