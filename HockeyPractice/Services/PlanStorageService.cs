using HockeyPractice.Infrastructure;
using Microsoft.Extensions.Options;

namespace HockeyPractice.Services;

public record StorageResult(bool Ok, string? Error = null, long Bytes = 0);

/// <summary>
/// Owns every read and write of plan PDFs. Kept deliberately narrow (Save / Open / Delete /
/// usage) so moving off the 1 GiB volume to object storage later touches only this class.
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

    /// <summary>Removes a team's entire directory — logo, every plan PDF, the lot.</summary>
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
}
