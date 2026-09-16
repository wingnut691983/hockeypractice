using Microsoft.Extensions.Configuration;

namespace HockeyPractice.Infrastructure;

/// <summary>
/// Resolves every on-disk location the app writes to.
///
/// On UpTurtle the only durable storage is the 1 GiB volume mounted at /persisted-data —
/// anything written elsewhere is wiped on every redeploy. Locally DATA_DIR points at ./data
/// so development needs no volume. Nothing in the app should build a write path by hand;
/// go through here so the two environments can't drift.
/// </summary>
public class DataPaths
{
    public DataPaths(IConfiguration config)
    {
        Root = config["DATA_DIR"] is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : "/persisted-data";

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(KeyRing);
        Directory.CreateDirectory(TeamsRoot);
    }

    /// <summary>Root of the persistent volume.</summary>
    public string Root { get; }

    /// <summary>SQLite database file.</summary>
    public string Database => Path.Combine(Root, "hockeypractice.db");

    public string ConnectionString => $"Data Source={Database}";

    /// <summary>
    /// Data Protection key ring. Must persist: these keys encrypt the access cookie, so
    /// regenerating them on each deploy would sign every player out and force the whole
    /// team to re-enter the team code.
    /// </summary>
    public string KeyRing => Path.Combine(Root, "dpkeys");

    public string TeamsRoot => Path.Combine(Root, "teams");

    /// <summary>
    /// Touched when a restore completes. The nightly archive reads it so its startup catch-up
    /// does not immediately back up a just-restored volume and spend a retention slot on it
    /// while someone is still deciding whether the restore was the right one.
    ///
    /// A file rather than the mtime of the kept .replaced database, which looks like it would do
    /// the same job and does not: File.Move preserves the modified time, so that value is when
    /// the OLD database was last written, which on a quiet site can be days before the restore.
    /// </summary>
    public string RestoreMarker => Path.Combine(Root, "last-restore");

    /// <summary>When the last restore finished, or null if this volume has never had one.</summary>
    public DateTime? LastRestoreUtc =>
        File.Exists(RestoreMarker) ? File.GetLastWriteTimeUtc(RestoreMarker) : null;

    public string TeamDirectory(int teamId) => Path.Combine(TeamsRoot, teamId.ToString());

    public string PlanDirectory(int teamId, int planId) =>
        Path.Combine(TeamDirectory(teamId), "plans", planId.ToString());

    /// <summary>
    /// The single PDF for a plan, keyed on the plan's row id.
    ///
    /// This is the LEGACY layout, still read and never written. Plans uploaded before PDFs became
    /// content-addressed live here, and so does any plan a restore brings back from an archive
    /// taken before that change. It is not going away: a restore rolls rows back while only ever
    /// adding files, so a one-time move of these into the store below would turn every one of
    /// those rows into a plan whose file cannot be found.
    /// </summary>
    public string PlanPdf(int teamId, int planId) => Path.Combine(PlanDirectory(teamId, planId), "plan.pdf");

    /// <summary>
    /// Where a team's plan PDFs live, named by the SHA-256 of their contents. Two plans holding the
    /// same file — which is what duplicating a plan produces — share one file here, and neither
    /// knows about the other.
    ///
    /// Per team rather than one store for the site, deliberately. A shared store would mean one
    /// team's upload could be removed by another team's deletion, and would let the existence of a
    /// file leak across teams. It also keeps deleting a team a single recursive directory delete.
    /// </summary>
    public string TeamPdfStore(int teamId) => Path.Combine(TeamDirectory(teamId), "pdfs");

    /// <summary>
    /// One content-addressed plan PDF. <paramref name="key"/> is the lower-case hex SHA-256 of the
    /// file and is checked rather than trusted: it reaches here from a database column, and a
    /// restored or hand-edited row must not be able to steer a read or a delete out of the store.
    /// </summary>
    public string TeamPdf(int teamId, string key)
    {
        if (!IsPdfKey(key))
            throw new ArgumentException($"'{key}' is not a plan PDF key.", nameof(key));

        return Path.Combine(TeamPdfStore(teamId), key + ".pdf");
    }

    /// <summary>A SHA-256 as we write it: 64 lower-case hex characters, nothing else.</summary>
    public static bool IsPdfKey(string? key) =>
        key is { Length: 64 } && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string DrillDirectory(int teamId, int drillId) =>
        Path.Combine(TeamDirectory(teamId), "drills", drillId.ToString());

    /// <summary>
    /// A drill's diagram. Unlike PlanPdf the filename is not fixed — a diagram may be an image or
    /// a PDF, so the name (with its extension) is stored on the Drill row, the way a team logo is.
    /// </summary>
    public string DrillDiagram(int teamId, int drillId, string fileName) =>
        Path.Combine(DrillDirectory(teamId, drillId), fileName);

    /// <summary>Total bytes currently used under the persistent root.</summary>
    public long UsedBytes()
    {
        try
        {
            return new DirectoryInfo(Root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }
}
