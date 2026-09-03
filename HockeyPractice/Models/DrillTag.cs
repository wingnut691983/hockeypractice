using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// A free-form label on a drill — a skill or a drill family — so the library can be filtered when
/// building a plan.
///
/// Deliberately separate from PlanTag rather than one polymorphic tag table: the two are searched
/// independently and two small parallel tables index better and read more plainly than one clever
/// one. They are separate vocabularies, and the UI labels them as such.
/// </summary>
public class DrillTag
{
    public int Id { get; set; }

    public int DrillId { get; set; }
    public Drill? Drill { get; set; }

    [Required, MaxLength(40)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Trimmed + lower-cased form of Name, used for case-insensitive search and duplicate
    /// detection. EF Core's SQLite provider translates string.Contains() to instr(), which is
    /// case-sensitive — searching and deduping on this column instead of Name is what makes
    /// "Backcheck" and "backcheck" behave as one tag.
    /// </summary>
    [Required, MaxLength(40)]
    public string NormalizedName { get; set; } = string.Empty;
}
