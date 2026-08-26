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
}
