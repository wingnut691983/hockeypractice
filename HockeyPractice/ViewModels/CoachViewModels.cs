using HockeyPractice.Models;

namespace HockeyPractice.ViewModels;

public class ManageViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public List<PlanCard> Plans { get; init; } = new();
    public List<Player> Roster { get; init; } = new();
    public int ConfirmedSubscribers { get; init; }

    public long UsedBytes { get; init; }
    public long QuotaBytes { get; init; }
    public double UsedFraction => QuotaBytes <= 0 ? 0 : (double)UsedBytes / QuotaBytes;
    public bool StorageTight => UsedFraction >= 0.75;

    public string? ViewCode { get; init; }
    public string? Notice { get; init; }

    /// <summary>Distinct tag names used anywhere on the team, for the browse row and search box.</summary>
    public List<string> AllTags { get; init; } = new();
    /// <summary>The tag currently filtering the list, if any (from the search box or a clicked chip).</summary>
    public string? ActiveTag { get; init; }
}

public class PlanEditViewModel
{
    public TeamContext Ctx { get; init; } = null!;
    public PracticePlan? Plan { get; init; }
    public List<PlanLink> Links { get; init; } = new();
    public string? Error { get; init; }
    public bool IsNew => Plan is null;

    /// <summary>Pre-filled with the next plausible practice slot so the coach rarely edits it.</summary>
    public DateTime DefaultDate { get; init; }
    public long MaxUploadBytes { get; init; }

    /// <summary>
    /// What the coach had typed when a new-plan upload failed validation. Files can't be
    /// re-populated, but losing a typed title and note over picking the wrong file would
    /// mean retyping it all on a phone keyboard.
    /// </summary>
    public string? RetainedTitle { get; init; }
    public string? RetainedLocation { get; init; }
    public string? RetainedNotes { get; init; }
    public List<string>? RetainedTags { get; init; }

    /// <summary>Which kind of plan this is. Chosen at creation and not switched afterwards.</summary>
    public PlanKind Kind { get; init; } = PlanKind.Pdf;
    public bool IsDrillPlan => Kind == PlanKind.Drills;

    /// <summary>The drills in this plan, in order.</summary>
    public List<DrillCard> PlanDrills { get; init; } = new();

    /// <summary>The team's library, filtered by ActiveDrillTag, offered for adding.</summary>
    public List<DrillCard> Library { get; init; } = new();

    /// <summary>Drill tags — a separate vocabulary from the plan tags above, hence the name.</summary>
    public List<string> AllDrillTags { get; init; } = new();
    public string? ActiveDrillTag { get; init; }

    /// <summary>Distinct tag names used anywhere on the team, for the tag field's autocomplete.</summary>
    public List<string> AllTags { get; init; } = new();

    /// <summary>The tags to show as chips — what was typed if a save bounced, else what's saved.</summary>
    public List<string> TagsValue => RetainedTags
        ?? Plan?.Tags.OrderBy(x => x.Name).Select(x => x.Name).ToList()
        ?? new List<string>();

    public string? Notice { get; init; }
}
