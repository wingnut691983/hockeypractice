using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

public class PracticePlan
{
    public int Id { get; set; }

    public int TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(140)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Local wall-clock time of the practice, NOT UTC. A practice is at 6:15pm at the rink
    /// regardless of where the server thinks it is; converting would move it.
    /// </summary>
    public DateTime PracticeDateLocal { get; set; }

    [MaxLength(120)] public string? Location { get; set; }
    [MaxLength(2000)] public string? CoachNotes { get; set; }

    /// <summary>
    /// Original upload filename, used for the download's Content-Disposition. Null on a
    /// drill-built plan, which has no uploaded file at all.
    /// </summary>
    [MaxLength(200)]
    public string? OriginalFileName { get; set; }

    /// <summary>Size of the uploaded PDF. Stays 0 for a drill-built plan.</summary>
    public long ByteSize { get; set; }

    /// <summary>
    /// Whether this plan is an uploaded PDF or built from drills. Chosen when the plan is created
    /// and not switched afterwards — the two render through entirely different paths.
    /// </summary>
    public PlanKind Kind { get; set; } = PlanKind.Pdf;

    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public DateTime? PublishedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<PlanLink> Links { get; set; } = new();
    public List<PlanView> Views { get; set; } = new();
    public List<PlanTag> Tags { get; set; } = new();

    /// <summary>The drills making up this plan, in order. Empty for a PDF plan.</summary>
    public List<PlanDrill> Drills { get; set; } = new();
}
