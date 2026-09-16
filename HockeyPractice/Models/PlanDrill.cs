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

    /// <summary>
    /// Minutes this plan adds to the drill's own run time, or null when it runs for the library
    /// time as it stands. Never zero and never negative: a plan can lengthen a drill, not shorten
    /// one.
    ///
    /// What is stored is the addition, not the answer. Re-timing the drill in the library then
    /// keeps whatever allowance the coach made here: 15 + 10 becomes 12 + 10, not a stale 25.
    ///
    /// It doubles as an absolute time for a drill that has none. Plenty of drills pre-date the run
    /// time rule, and one of those contributes nothing to a plan total; setting this gives it a
    /// length in THIS plan without inventing one for every other plan using it. Same column,
    /// because it is the same arithmetic either way: (base ?? 0) + extra. See RunTime.Effective.
    ///
    /// It lives on this row rather than on the Drill because the same drill legitimately appears
    /// twice in one practice (see above), so PlanDrill.Id is the only thing that identifies "this
    /// drill, this time, in this plan". Reordering swaps SortOrder and leaves Id alone, which is
    /// what keeps the time with the row a coach set it on.
    /// </summary>
    public int? ExtraRunTimeMinutes { get; set; }
}
