using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// One row the first time a viewer actually renders a plan. Recorded against the roster player
/// when one has been picked, otherwise against an opaque per-device key.
/// </summary>
public class PlanView
{
    public int Id { get; set; }

    public int PracticePlanId { get; set; }
    public PracticePlan? PracticePlan { get; set; }

    public int? PlayerId { get; set; }
    public Player? Player { get; set; }

    /// <summary>Opaque per-device identifier. Not derived from IP or any personal data.</summary>
    [Required, MaxLength(64)]
    public string ViewerKey { get; set; } = string.Empty;

    public DateTime FirstViewedUtc { get; set; } = DateTime.UtcNow;
}
