using System.ComponentModel.DataAnnotations;

namespace HockeyPractice.Models;

/// <summary>
/// One picture (or PDF) attached to a drill. A drill can carry several — a progression often
/// needs a diagram per stage, which a single attachment couldn't express.
///
/// Ordered by Id, which is the order they were added. Deliberately no SortOrder column: uploads
/// arrive in the order the coach picked them, that is the order they should read in, and a
/// reorder control would be a second way to arrange something that is already right.
/// </summary>
public class DrillDiagram
{
    public int Id { get; set; }

    public int DrillId { get; set; }
    public Drill? Drill { get; set; }

    /// <summary>
    /// Filename including its extension, since a diagram may be an image or a PDF. The file lives
    /// in the drill's own directory, so this is all that is needed to find it again.
    /// </summary>
    [Required, MaxLength(120)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Stored size, summed per drill so quota use stays visible.</summary>
    public long Bytes { get; set; }

    public bool IsPdf => FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
}
