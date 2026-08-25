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

    public string TeamDirectory(int teamId) => Path.Combine(TeamsRoot, teamId.ToString());

    public string PlanDirectory(int teamId, int planId) =>
        Path.Combine(TeamDirectory(teamId), "plans", planId.ToString());

    /// <summary>The single PDF for a plan. One file per plan; deleting the directory deletes the plan.</summary>
    public string PlanPdf(int teamId, int planId) => Path.Combine(PlanDirectory(teamId, planId), "plan.pdf");

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
