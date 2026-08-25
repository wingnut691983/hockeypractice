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

    /// <summary>Original upload filename, used for the download's Content-Disposition.</summary>
    [Required, MaxLength(200)]
    public string OriginalFileName { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public DateTime? PublishedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public List<PlanLink> Links { get; set; } = new();
    public List<PlanView> Views { get; set; } = new();
}
