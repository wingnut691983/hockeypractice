using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace HockeyPractice.Models;

/// <summary>
/// The editable pieces behind a drawn drill picture: the rink view and every line and stamp, as
/// the JSON the drawing editor (wwwroot/js/drill-sketch.js) reads and writes. A picture with no
/// row here was uploaded or pasted, and behaves exactly as it always has.
///
/// Its own table rather than a column on DrillDiagram, because EF loads every column and the plan
/// view, the drill list and print all Include(d => d.Diagrams). A column would carry every
/// drawing's JSON onto every player's plan view for nothing. Only the drill edit page and the copy
/// path read this table. Not a file beside the picture either: a row is in the nightly backup,
/// restores consistently with its picture row, and is deleted with it by cascade, with nothing to
/// add to PlanStorageService.
///
/// The server never interprets the drawing beyond checking it: it validates, stores, and hands it
/// back whole. Plain TEXT, not SQLite JSONB, for that reason.
/// </summary>
public class DrillDiagramSketch
{
    /// <summary>Primary key and foreign key both: a picture has at most one drawing.</summary>
    [Key]
    public int DrillDiagramId { get; set; }
    public DrillDiagram? Diagram { get; set; }

    [Required]
    public string Json { get; set; } = string.Empty;

    /// <summary>
    /// A typical drawing is 2 to 8 KB. The cap is generous for a busy one and small enough that a
    /// hand-posted blob can't use the database as storage.
    /// </summary>
    public const int MaxJsonBytes = 64 * 1024;

    private static readonly HashSet<string> Rinks = new(StringComparer.Ordinal)
    {
        "full", "fullh", "half", "zone"
    };

    /// <summary>
    /// Checks a drawing posted by the editor. Returns the JSON to store and alt text for the
    /// picture, or an error. Only the envelope is checked (version, rink, an item list): the
    /// editor sanitises every item it reads, and nothing here ever renders the JSON as HTML.
    /// </summary>
    public static bool TryRead(string? json, out string stored, out string alt, out string error)
    {
        stored = alt = error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the drawing was empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
        {
            error = "the drawing is too detailed to save. Remove a few lines and try again.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("rink", out var rink) || rink.ValueKind != JsonValueKind.String
                || !Rinks.Contains(rink.GetString()!)
                || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                error = "the drawing wasn't in a shape this site understands.";
                return false;
            }

            stored = json.Trim();
            alt = Describe(rink.GetString()!, items);
            return true;
        }
        catch (JsonException)
        {
            error = "the drawing wasn't in a shape this site understands.";
            return false;
        }
    }

    /// <summary>
    /// Alt text from what is drawn, e.g. "Half rink drawing: 3 players, 2 passes, 1 shot". Better
    /// than "Diagram for ..." for a screen reader, and the only description a drawing has.
    /// </summary>
    private static string Describe(string rink, JsonElement items)
    {
        int players = 0, opponents = 0, coaches = 0, pucks = 0, cones = 0, blockers = 0,
            skating = 0, passes = 0, shots = 0;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var t = Str(item, "t");

            if (t == "mark")
            {
                switch (Str(item, "kind"))
                {
                    case "X" or "F" or "C" or "W" or "D" or "G": players++; break;
                    case "O": opponents++; break;
                    case "coach": coaches++; break;
                    case "puck": pucks++; break;
                    case "cone": cones++; break;
                    case "blocker": blockers++; break;
                }
            }
            else if (t == "path")
            {
                switch (Str(item, "style"))
                {
                    case "pass": passes++; break;
                    case "shot": shots++; break;
                    default: skating++; break;
                }
            }
        }

        var parts = new List<string>();
        void Add(int n, string one, string many) { if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}"); }
        Add(players, "player", "players");
        Add(opponents, "opponent", "opponents");
        Add(coaches, "coach", "coaches");
        Add(skating, "skating line", "skating lines");
        Add(passes, "pass", "passes");
        Add(shots, "shot", "shots");
        Add(pucks, "puck", "pucks");
        Add(cones, "cone", "cones");
        Add(blockers, "blocker", "blockers");

        var view = rink switch
        {
            "half" => "Half rink",
            "zone" => "One zone",
            _ => "Full rink"
        };

        return parts.Count == 0 ? $"{view} drawing" : $"{view} drawing: {string.Join(", ", parts)}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
