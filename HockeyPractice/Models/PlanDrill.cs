namespace HockeyPractice.Models;

/// <summary>
/// One drill's place in one plan.
///
/// Intentionally has NO unique constraint on (PracticePlanId, DrillId): the same drill legitimately
/// appears twice in a practice — a skating drill used as both warm-up and cool-down — so the picker
/// always offers "Add" rather than a checkmark.
/// </summary>
public class PlanDrill
{
    public int Id { get; set; }

    public int PracticePlanId { get; set; }
    public PracticePlan? PracticePlan { get; set; }

    public int DrillId { get; set; }
    public Drill? Drill { get; set; }

    /// <summary>
    /// Position in the plan. Not unique and not necessarily contiguous — reordering swaps two
    /// values, and two tabs adding at once can compute the same "max + 1". Every query therefore
    /// orders by SortOrder then Id so the order is stable regardless.
    /// </summary>
    public int SortOrder { get; set; }
}
