using HockeyPractice.Models;

namespace HockeyPractice.ViewModels;

/// <summary>
/// One drill as a row or a plan entry. Shared by the library, the plan editor's picker, and the
/// player-facing plan, so those three can't drift apart in what they show.
/// </summary>
public class DrillCard
{
    public Drill Drill { get; init; } = null!;

    /// <summary>Set only when this card is a drill inside a plan — the row to reorder or remove.</summary>
    public int? PlanDrillId { get; init; }

    /// <summary>The drill's diagrams in upload order — the order they should be read in.</summary>
    public List<DrillDiagram> Diagrams =>
        Drill.Diagrams.OrderBy(d => d.Id).ToList();

    public bool HasDiagram => Drill.Diagrams.Count > 0;
    public int DiagramCount => Drill.Diagrams.Count;
    public long DiagramBytes => Drill.Diagrams.Sum(d => d.Bytes);

    /// <summary>
    /// Player URL when the video can be framed, null when it can't (or there's no video). Resolved
    /// once in the controller rather than per row in the view.
    /// </summary>
    public string? EmbedUrl { get; init; }

    public bool HasVideo => !string.IsNullOrWhiteSpace(Drill.VideoUrl);
}

/// <summary>
/// How long a set of drills runs. Carries the count of drills with no estimate alongside the
/// sum, so the total can say what it is missing instead of silently under-reporting.
/// </summary>
public class RunTimeTotal
{
    public int Minutes { get; init; }
    public int MissingCount { get; init; }

    public bool HasAnything => Minutes > 0 || MissingCount > 0;
    public string Label => HockeyPractice.Infrastructure.RunTime.PlanTotal(Minutes, MissingCount);

    public static RunTimeTotal From(IEnumerable<DrillCard> cards)
    {
        var list = cards.ToList();
        return new RunTimeTotal
        {
            Minutes = list.Sum(c => c.Drill.RunTimeMinutes ?? 0),
            MissingCount = list.Count(c => c.Drill.RunTimeMinutes is null)
        };
    }
}

public class DrillListViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public List<DrillCard> Drills { get; init; } = new();

    public List<string> AllTags { get; init; } = new();
    public string? ActiveTag { get; init; }

    /// <summary>Showing the archived drills rather than the working library.</summary>
    public bool ShowingArchived { get; init; }

    /// <summary>
    /// Teams this browser also holds manager access to. Empty is a normal state, not an error —
    /// the view explains how to add one rather than showing an empty dropdown.
    /// </summary>
    public List<TeamLink> CopyTargets { get; init; } = new();

    public string? Notice { get; init; }
}

public class DrillEditViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public Drill? Drill { get; init; }
    public bool IsNew => Drill is null;

    public List<string> AllTags { get; init; } = new();

    /// <summary>Where to go after saving — set when creating a drill from inside the plan editor.</summary>
    public string? ReturnUrl { get; init; }

    public string? Error { get; init; }
    public string? Notice { get; init; }

    /// <summary>
    /// What the coach had typed when a save failed. A rejected diagram must not cost them a long
    /// description they just wrote out.
    /// </summary>
    public string? RetainedTitle { get; init; }
    public string? RetainedDescription { get; init; }
    public string? RetainedVideoUrl { get; init; }
    public string? RetainedRunTime { get; init; }
    public string? RetainedTags { get; init; }

    public string TitleValue => RetainedTitle ?? Drill?.Title ?? "";
    public string DescriptionValue => RetainedDescription ?? Drill?.Description ?? "";
    public string VideoUrlValue => RetainedVideoUrl ?? Drill?.VideoUrl ?? "";
    public string RunTimeValue => RetainedRunTime ?? Drill?.RunTimeMinutes?.ToString() ?? "";

    public string TagsValue => RetainedTags
        ?? (Drill?.Tags is { Count: > 0 } t
            ? string.Join(", ", t.OrderBy(x => x.Name).Select(x => x.Name))
            : "");

    /// <summary>How many plans use this drill — shown so the coach knows what a change affects.</summary>
    public int UsedInPlans { get; init; }
}
