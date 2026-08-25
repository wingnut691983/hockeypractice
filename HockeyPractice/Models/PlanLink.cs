using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HockeyPractice.Models;

/// <summary>
/// A link pulled out of the uploaded PDF at upload time — both real link annotations and
/// URLs the coach typed as plain text. Surfaced as a tappable card so players don't have to
/// hit a tiny link inside a PDF on a phone.
/// </summary>
public class PlanLink
{
    public int Id { get; set; }

    public int PracticePlanId { get; set; }
    public PracticePlan? PracticePlan { get; set; }

    [Required, MaxLength(1000)]
    public string Url { get; set; } = string.Empty;

    /// <summary>What this video is — the hyperlink's anchor text, or the line it sits on.</summary>
    [MaxLength(140)]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The block of the practice it belongs to — the nearest heading above it in the document
    /// ("Breakouts", "3. Small-area games"). Null when the plan has no discernible structure.
    /// </summary>
    [MaxLength(140)]
    public string? Section { get; set; }

    public LinkKind Kind { get; set; } = LinkKind.Other;

    /// <summary>Provider video id, when recognised — used to build a thumbnail.</summary>
    [MaxLength(40)]
    public string? VideoId { get; set; }

    /// <summary>
    /// The title as published on YouTube/Vimeo, looked up at upload time. Kept separately from
    /// Label so a coach's own wording is never overwritten, and so the lookup can be re-run.
    /// </summary>
    [MaxLength(140)]
    public string? VideoTitle { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// True when Label came from the document's own words rather than a fallback. Not
    /// persisted — it exists so a looked-up video title replaces a generated placeholder
    /// without ever overwriting what the coach actually wrote.
    /// </summary>
    [NotMapped]
    public bool LabelFromDocument { get; set; }

    /// <summary>Coach can hide a link extraction picked up from a footer or boilerplate.</summary>
    public bool IsHidden { get; set; }
}
