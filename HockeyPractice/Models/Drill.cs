using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// A reusable drill in a team's library: what it is, how it's run, an optional diagram and video.
/// Plans are assembled from these instead of being drawn from scratch each week.
///
/// Owned by one team and COPIED when shared to another, rather than referenced by several. A drill
/// run with a 16U side and a 12U side often should differ, and a shared row meant editing it for
/// one team silently rewrote another team's already-published plan. Copies cost little now that
/// diagrams are shrunk on upload (~150 KB each), and <see cref="CopiedFromDrillId"/> keeps a record
/// of where a copy came from.
/// </summary>
public class Drill
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(140)]
    public string Title { get; set; } = string.Empty;

    /// <summary>How the drill is run. Free text; rendered with line breaks preserved.</summary>
    [MaxLength(4000)]
    public string? Description { get; set; }

    /// <summary>
    /// The drill's diagrams, in the order they were added. A progression usually needs one per
    /// stage, so this is a collection rather than a single attachment.
    /// </summary>
    public List<DrillDiagram> Diagrams { get; set; } = new();

    /// <summary>
    /// Roughly how long the drill takes to run, in minutes. Summed across a plan so a coach can
    /// see whether the practice fits the ice time they've booked.
    ///
    /// Nullable on purpose: an estimate the coach hasn't made yet is different from zero minutes,
    /// and a plan total has to be able to say it is missing some drills rather than quietly
    /// under-reporting.
    /// </summary>
    public int? RunTimeMinutes { get; set; }

    /// <summary>
    /// Optional link to a video of the drill. YouTube and Vimeo links play in the plan's own popup
    /// player; anything else opens in a new tab. No link means no button at all.
    /// </summary>
    [MaxLength(500)]
    public string? VideoUrl { get; set; }

    /// <summary>
    /// Hidden from the library and the plan picker, while plans already using it still render.
    /// This is the offered alternative to deleting a drill that a plan depends on.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// The drill this one was copied from, for reference only. Deliberately not a foreign key: the
    /// original belongs to another team and may be deleted, and a copy must never cascade from it.
    /// </summary>
    public int? CopiedFromDrillId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<DrillTag> Tags { get; set; } = new();
}
