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

    /// <summary>
    /// The file name's GUID, for the <c>?v=</c> token on the Diagram URL. Null when the name
    /// doesn't match the shape this app writes, which makes the response revalidate instead of
    /// being cached hard.
    ///
    /// The URL cannot carry the file name itself: it is keyed on this row's id
    /// (<c>/drills/{id}/diagram/{diagramId}</c>), so the same address can outlive the file behind
    /// it. Today nothing reassigns <see cref="FileName"/> — it is written once, at insert, and
    /// removing a diagram deletes the row — so the address is stable by construction. The token
    /// exists for the case that isn't: a restore rolls the DrillDiagrams ids back and reissues
    /// them to different pictures, so a browser told "immutable, one year" against a bare URL
    /// would paint the wrong diagram until the cache expired. Same reasoning as
    /// PracticePlan.OverviewVersion, which has the same hazard for the same reason.
    ///
    /// Length-checked because this reads from a database column: 8 for "diagram-" plus 32 hex,
    /// with the extension (".webp" or ".pdf") off first so one check covers both.
    /// </summary>
    public string? Version =>
        Path.GetFileNameWithoutExtension(FileName) is { Length: 40 } n
        && n.StartsWith("diagram-", StringComparison.Ordinal)
            ? n[8..]
            : null;
}
