namespace HockeyPractice.Services;

/// <summary>One archive sitting in the bucket.</summary>
/// <param name="Key">Full object key, prefix included. This is what Delete and Download take.</param>
/// <param name="Name">Just the filename, for showing on the admin page.</param>
public record StoredBackup(string Key, string Name, long Bytes, DateTime CreatedUtc);

/// <summary>
/// Somewhere off UpTurtle to keep archives. Narrow on purpose: four operations is the whole of
/// what the scheduler and the admin page need, and it keeps the S3 vocabulary from leaking into
/// either of them.
/// </summary>
public interface IBackupStore
{
    /// <summary>False when the app has no credentials, which is the normal state locally.</summary>
    bool Enabled { get; }

    /// <summary>Full object key for an archive filename, prefix applied.</summary>
    string KeyFor(string name);

    /// <summary>Every archive under the configured prefix, newest first.</summary>
    Task<IReadOnlyList<StoredBackup>> ListAsync(CancellationToken ct = default);

    Task UploadAsync(string localPath, string key, CancellationToken ct = default);

    Task DownloadAsync(string key, string localPath, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Size of the stored object, or null if it is not there. This is the confirmation step after
    /// an upload: the retention prune must never run on the strength of a PUT that returned
    /// without the object actually landing.
    /// </summary>
    Task<long?> SizeAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Stands in when no bucket is configured, following the same pattern as
/// <see cref="LoggingEmailSender"/>: the app runs, the admin page says archiving is off, and
/// nothing throws. Locally this is the normal case.
/// </summary>
public class NullBackupStore : IBackupStore
{
    public bool Enabled => false;

    public string KeyFor(string name) => name;

    public Task<IReadOnlyList<StoredBackup>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredBackup>>(Array.Empty<StoredBackup>());

    public Task UploadAsync(string localPath, string key, CancellationToken ct = default) =>
        throw new InvalidOperationException("Off-site backups are not configured.");

    public Task DownloadAsync(string key, string localPath, CancellationToken ct = default) =>
        throw new InvalidOperationException("Off-site backups are not configured.");

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        throw new InvalidOperationException("Off-site backups are not configured.");

    public Task<long?> SizeAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<long?>(null);
}
