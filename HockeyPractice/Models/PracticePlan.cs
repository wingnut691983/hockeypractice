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

    /// <summary>
    /// Size of the uploaded PDF. Stays 0 for a drill-built plan.
    ///
    /// This plan's PDF, not this plan's share of the disk: two plans pointing at the same PdfKey
    /// each report the full size while the volume holds one copy. The storage meter measures the
    /// volume itself, so it stays honest regardless.
    /// </summary>
    public long ByteSize { get; set; }

    /// <summary>
    /// SHA-256 of the plan's PDF, lower-case hex — the name of the file in the team's PDF store.
    /// Two plans carrying the same document share one file and neither points at the other.
    ///
    /// Null means one of two things, and both are normal: a drill-built plan has no file at all,
    /// and a plan uploaded before PDFs were content-addressed still reads the legacy per-plan
    /// path. See DataPaths.PlanPdf for why that path is permanent.
    /// </summary>
    [MaxLength(64)]
    public string? PdfKey { get; set; }

    /// <summary>
    /// One optional picture showing the shape of the whole practice, for a plan whose drills run
    /// as simultaneous stations and so cannot be read as a sequence. Filename including its
    /// extension, always WebP, always server-generated, living in the plan's own directory the way
    /// a drill diagram lives in the drill's. Null means the plan has none and nothing renders.
    ///
    /// Drill plans only. A PDF plan is already a document and has no running order to overview.
    /// </summary>
    [MaxLength(120)]
    public string? OverviewFileName { get; set; }

    /// <summary>Stored size of the overview picture, after shrinking. 0 when there is none.</summary>
    public long OverviewBytes { get; set; }

    public bool HasOverview => !string.IsNullOrEmpty(OverviewFileName);

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
