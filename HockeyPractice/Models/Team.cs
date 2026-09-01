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

    /// <summary>
    /// Site admin's chosen display order on the team picker and the admin dashboard. Lower
    /// sorts first. Not unique or contiguous — moving a team up/down just swaps this value with
    /// its neighbour's, so gaps and ties are expected and harmless; every ordered query breaks
    /// ties on Name for a stable result.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Link to the team's shared/collaborative Spotify playlist (e.g. locker-room music).
    /// Validated to be an open.spotify.com playlist link on save. Not gated by role — every
    /// viewer with team access sees it; granting "add song" permission happens entirely inside
    /// Spotify's own app, not here.
    /// </summary>
    [MaxLength(300)]
    public string? SpotifyPlaylistUrl { get; set; }

    /// <summary>
    /// Same defence as SafePrimary/SafeAccent: the controller validates on save, but a value
    /// that reached this column any other way (a future admin tool, a manual DB edit) should
    /// lose the team its playlist card rather than land a javascript:/data: URL in an href.
    /// </summary>
    [NotMapped]
    public string? SafeSpotifyPlaylistUrl =>
        SpotifyPlaylistUrl is not null && SpotifyPlaylistRegex.IsMatch(SpotifyPlaylistUrl)
            ? SpotifyPlaylistUrl
            : null;

    private static readonly Regex SpotifyPlaylistRegex =
        new(@"^https://open\.spotify\.com/(intl-[a-z]{2}/)?playlist/[A-Za-z0-9]+(\?\S*)?$",
            RegexOptions.IgnoreCase);

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<Player> Players { get; set; } = new();
    public List<PracticePlan> Plans { get; set; } = new();
    public List<Subscriber> Subscribers { get; set; } = new();
}
