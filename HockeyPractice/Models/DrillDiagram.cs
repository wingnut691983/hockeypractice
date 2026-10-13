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

    /// <summary>
    /// Alt text for a drawn picture, generated from what is drawn ("Half rink drawing: 3 players,
    /// 2 passes"). Null for uploaded pictures, whose views fall back to "Diagram for ...". On this
    /// row rather than in <see cref="Sketch"/> because the plan views need it and must not load
    /// the drawing's JSON to get it.
    /// </summary>
    [MaxLength(200)]
    public string? AltText { get; set; }

    /// <summary>
    /// The drawing behind this picture, when it was drawn here. Never Include()d by the read
    /// paths; see DrillDiagramSketch for why it is a separate table.
    /// </summary>
    public DrillDiagramSketch? Sketch { get; set; }

    public bool IsPdf => FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file name's GUID, for the <c>?v=</c> token on the Diagram URL. Null when the name
    /// doesn't match the shape this app writes, which makes the response revalidate instead of
    /// being cached hard.
    ///
    /// The URL cannot carry the file name itself: it is keyed on this row's id
    /// (<c>/drills/{id}/diagram/{diagramId}</c>), so the same address can outlive the file behind
    /// it. Exactly one path reassigns <see cref="FileName"/>: saving an edited drawing
    /// (DrillController.ReplaceSketch) writes a new file under a fresh GUID and points this row at
    /// it, so the picture keeps its id and its place in the order. The fresh GUID is what makes
    /// that safe, because it changes this token and no browser goes on believing the old bytes
    /// are current. Anything else that ever reassigns FileName must do the same. The token also
    /// covers the case nobody chose: a restore rolls the DrillDiagrams ids back and reissues
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
