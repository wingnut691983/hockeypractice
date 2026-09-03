using HockeyPractice.Infrastructure;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace HockeyPractice.Services;

public record StorageResult(bool Ok, string? Error = null, long Bytes = 0);

/// <summary>Result of storing a drill diagram. FileName carries the extension, so it is what the
/// Drill row must remember in order to find and serve the file again.</summary>
public record DiagramResult(bool Ok, string? Error = null, long Bytes = 0, string? FileName = null);

/// <summary>
/// Owns every read and write of uploaded files — plan PDFs and drill diagrams. Kept deliberately
/// narrow (Save / Open / Delete / usage) so moving off the 1 GiB volume to object storage later
/// touches only this class.
/// </summary>
public class PlanStorageService
{
    // Every PDF starts with these five bytes. Checked instead of trusting the file extension
    // or the browser-supplied content type, both of which are trivially wrong or spoofed.
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    private readonly DataPaths _paths;
    private readonly SiteOptions _options;
    private readonly ILogger<PlanStorageService> _log;

    public PlanStorageService(DataPaths paths, IOptions<SiteOptions> options, ILogger<PlanStorageService> log)
    {
        _paths = paths;
        _options = options.Value;
        _log = log;
    }

    public long QuotaBytes => _options.StorageQuotaBytes;
    public long UsedBytes() => _paths.UsedBytes();
    public double UsedFraction() => QuotaBytes <= 0 ? 0 : (double)UsedBytes() / QuotaBytes;

    /// <summary>
    /// True when the volume is too full to accept another upload. Refusing with an explanation
    /// beats letting a write fail with ENOSPC halfway through.
    /// </summary>
    public bool IsFull() => UsedFraction() >= _options.StorageWarnThreshold;

    public string? ValidateUpload(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return "Choose a PDF to upload.";

        if (file.Length > _options.MaxUploadBytes)
            return $"That file is {Human(file.Length)}. The limit is {Human(_options.MaxUploadBytes)}.";

        if (IsFull())
            return "Storage is nearly full. Delete some old practice plans before uploading a new one.";

        using var stream = file.OpenReadStream();
        Span<byte> header = stackalloc byte[5];
        if (stream.Read(header) < 5 || !header.SequenceEqual(PdfMagic))
        {
            return "That doesn't look like a PDF. In Word use File → Save As and pick PDF; " +
                   "in Google Docs use File → Download → PDF Document. Then upload the PDF.";
        }

        return null;
    }

    public async Task<StorageResult> SaveAsync(int teamId, int planId, IFormFile file, CancellationToken ct = default)
    {
        var dir = _paths.PlanDirectory(teamId, planId);
        Directory.CreateDirectory(dir);
        var target = _paths.PlanPdf(teamId, planId);

        try
        {
            await using var destination = File.Create(target);
            await using var source = file.OpenReadStream();
            await source.CopyToAsync(destination, ct);
            return new StorageResult(true, Bytes: destination.Length);
        }
        catch (IOException ex)
        {
            _log.LogError("Failed to write plan {PlanId} for team {TeamId}: {Error}", planId, teamId, ex.Message);
            TryDelete(target);
            return new StorageResult(false, "Could not save the file. The storage volume may be full.");
        }
    }

    public bool Exists(int teamId, int planId) => File.Exists(_paths.PlanPdf(teamId, planId));

    public Stream Open(int teamId, int planId) =>
        new FileStream(_paths.PlanPdf(teamId, planId), FileMode.Open, FileAccess.Read, FileShare.Read);

    public void DeletePlan(int teamId, int planId)
    {
        var dir = _paths.PlanDirectory(teamId, planId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not delete plan directory {Dir}: {Error}", dir, ex.Message);
        }
    }

    // ── Drill diagrams ───────────────────────────────────────────────────

    /// <summary>
    /// Stores a drill's diagram, shrinking it when it's an image.
    ///
    /// Validation sniffs the content rather than trusting the browser's content type — the same
    /// reasoning as the PDF path above, and the opposite of the logo path, which trusts the header
    /// it was handed. A PDF is stored as-is (it can't be shrunk); anything else must actually
    /// decode as an image, which is a stronger check than any extension or magic-byte table.
    /// </summary>
    public async Task<DiagramResult> SaveDiagramAsync(int teamId, int drillId, IFormFile file,
        CancellationToken ct = default)
    {
        if (file.Length == 0) return new DiagramResult(false, "That file is empty.");

        if (file.Length > _options.MaxDiagramBytes)
            return new DiagramResult(false,
                $"That file is {Human(file.Length)}. The limit is {Human(_options.MaxDiagramBytes)}.");

        if (IsFull())
            return new DiagramResult(false,
                "Storage is nearly full. Delete some old plans or drills before uploading more.");

        var isPdf = await LooksLikePdfAsync(file, ct);

        var dir = _paths.DrillDirectory(teamId, drillId);
        Directory.CreateDirectory(dir);

        // A fresh name each save, so a replaced diagram can't be served from a stale cache.
        var fileName = $"diagram-{Guid.NewGuid():N}{(isPdf ? ".pdf" : ".webp")}";
        var target = _paths.DrillDiagram(teamId, drillId, fileName);

        try
        {
            if (isPdf)
            {
                await using var destination = File.Create(target);
                await using var source = file.OpenReadStream();
                await source.CopyToAsync(destination, ct);
            }
            else
            {
                var error = await ShrinkToWebpAsync(file, target, ct);
                if (error is not null)
                {
                    TryDelete(target);
                    return new DiagramResult(false, error);
                }
            }

            return new DiagramResult(true, Bytes: new FileInfo(target).Length, FileName: fileName);
        }
        catch (IOException ex)
        {
            _log.LogError("Failed to write diagram for drill {DrillId}: {Error}", drillId, ex.Message);
            TryDelete(target);
            return new DiagramResult(false, "Could not save that file. The storage volume may be full.");
        }
    }

    /// <summary>
    /// Decodes, scales down if oversized, and re-encodes as WebP. Returns an error message when the
    /// upload isn't a usable image.
    /// </summary>
    private async Task<string?> ShrinkToWebpAsync(IFormFile file, string target, CancellationToken ct)
    {
        const string NotAnImage =
            "That needs to be a PDF or an image (JPEG, PNG, GIF or WebP). " +
            "A photo or screenshot of the diagram works well.";

        try
        {
            // Read the header first. A 50 KB file can declare a 20000x20000 image that would
            // expand to over a gigabyte once decoded and take the whole site down with it, so the
            // dimensions have to be rejected BEFORE anything is decoded.
            await using (var probe = file.OpenReadStream())
            {
                var info = await Image.IdentifyAsync(probe, ct);
                if ((long)info.Width * info.Height > _options.MaxDiagramPixels)
                {
                    return $"That image is {info.Width}x{info.Height}, which is too large to " +
                           "process. Scale it down or take a screenshot of it first.";
                }
            }

            await using var source = file.OpenReadStream();
            using var image = await Image.LoadAsync(source, ct);

            if (image.Width > _options.DiagramMaxWidth)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(_options.DiagramMaxWidth, 0)
                }));
            }

            await using var destination = File.Create(target);
            await image.SaveAsWebpAsync(destination, new WebpEncoder { Quality = 85 }, ct);
            return null;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException
                                      or NotSupportedException)
        {
            return NotAnImage;
        }
    }

    /// <summary>
    /// Peeks the first bytes for the PDF signature, then leaves the stream alone. Opens its own
    /// stream and disposes it, so the later save reads from a fresh one starting at position 0 —
    /// the same open/peek/dispose/reopen contract ValidateUpload relies on.
    /// </summary>
    private static async Task<bool> LooksLikePdfAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        var header = new byte[PdfMagic.Length];
        var read = await stream.ReadAsync(header, ct);
        return read == PdfMagic.Length && header.SequenceEqual(PdfMagic);
    }

    /// <summary>Removes a drill's directory and the diagram inside it.</summary>
    public void DeleteDrill(int teamId, int drillId)
    {
        var dir = _paths.DrillDirectory(teamId, drillId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not delete drill directory {Dir}: {Error}", dir, ex.Message);
        }
    }

    /// <summary>
    /// Duplicates a diagram for a copied drill. Copies rather than references so each team can
    /// edit or delete its own drill without touching anyone else's.
    /// </summary>
    public string? CopyDiagram(int fromTeamId, int fromDrillId, int toTeamId, int toDrillId,
        string fileName)
    {
        var source = _paths.DrillDiagram(fromTeamId, fromDrillId, fileName);
        if (!File.Exists(source)) return null;

        try
        {
            Directory.CreateDirectory(_paths.DrillDirectory(toTeamId, toDrillId));
            File.Copy(source, _paths.DrillDiagram(toTeamId, toDrillId, fileName), overwrite: true);
            return fileName;
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not copy diagram for drill {DrillId}: {Error}", fromDrillId, ex.Message);
            return null;
        }
    }

    /// <summary>Removes one diagram file — the old one, after a replacement is safely written.</summary>
    public void DeleteDiagram(int teamId, int drillId, string fileName) =>
        TryDelete(_paths.DrillDiagram(teamId, drillId, fileName));

    public bool DiagramExists(int teamId, int drillId, string fileName) =>
        File.Exists(_paths.DrillDiagram(teamId, drillId, fileName));

    public string DiagramPath(int teamId, int drillId, string fileName) =>
        _paths.DrillDiagram(teamId, drillId, fileName);

    /// <summary>Removes a team's entire directory — logo, every plan PDF, every drill, the lot.</summary>
    public void DeleteTeam(int teamId)
    {
        var dir = _paths.TeamDirectory(teamId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            _log.LogWarning("Could not delete team directory {Dir}: {Error}", dir, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} bytes"
    };
}

public class SiteOptions
{
    public string Name { get; set; } = "Practice Plans";
    public long MaxUploadBytes { get; set; } = 15 * 1024 * 1024;
    public long StorageQuotaBytes { get; set; } = 1024L * 1024 * 1024;
    public double StorageWarnThreshold { get; set; } = 0.9;

    /// <summary>Largest drill diagram accepted, before shrinking. PDFs can't be shrunk, so this
    /// is what actually caps them.</summary>
    public long MaxDiagramBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Widest a stored diagram image gets. Anything larger is scaled down — a phone photo of a
    /// whiteboard drops from megabytes to ~150 KB, which is what makes copying a whole library
    /// between teams cheap.
    /// </summary>
    public int DiagramMaxWidth { get; set; } = 1600;

    /// <summary>
    /// Pixel ceiling checked from the image HEADER before decoding. A 50 KB file can describe a
    /// 20000x20000 image that expands to well over a gigabyte in memory and takes the container
    /// down — a byte-size limit alone does not protect against that.
    /// </summary>
    public long MaxDiagramPixels { get; set; } = 50_000_000;
}
