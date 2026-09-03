using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// A free-form label a coach attaches to a plan — a skill or drill name — so past plans can be
/// found again by what they covered, not just when they happened.
/// </summary>
public class PlanTag
{
    public int Id { get; set; }

    public int PracticePlanId { get; set; }
    public PracticePlan? PracticePlan { get; set; }

    [Required, MaxLength(40)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Trimmed + lower-cased form of Name, used for case-insensitive search and duplicate
    /// detection. EF Core's SQLite provider translates string.Contains() to instr(), which is
    /// case-sensitive — searching/deduping on this column instead of Name is what makes
    /// "Backcheck" and "backcheck" behave as the same tag.
    /// </summary>
    [Required, MaxLength(40)]
    public string NormalizedName { get; set; } = string.Empty;
}
