using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace HockeyPractice.Services;

/// <summary>
/// Cloudflare R2, over its S3-compatible API.
///
/// Chosen over Google Drive, which needs a published OAuth app because service accounts have no
/// Drive quota on a consumer account. R2's S3 API means an ordinary AWS SDK client with three
/// settings changed, and no Cloudflare-specific code anywhere.
/// </summary>
public class S3BackupStore : IBackupStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ILogger<S3BackupStore> _log;

    public bool Enabled => true;

    public S3BackupStore(IConfiguration config, ILogger<S3BackupStore> log)
    {
        _log = log;

        // Trimmed, not because it is tidy but because it has already bitten: ARCHIVE_S3_ENDPOINT
        // was first stored with a leading space. The SDK parses ServiceURL as a URI, so that
        // fails in a way that presents as a credentials problem, which is a bad hour to spend.
        var endpoint = config["ARCHIVE_S3_ENDPOINT"]?.Trim() ?? "";
        _bucket = config["ARCHIVE_S3_BUCKET"]?.Trim() ?? "";
        var accessKey = config["ARCHIVE_S3_ACCESS_KEY_ID"]?.Trim() ?? "";
        var secretKey = config["ARCHIVE_S3_SECRET_ACCESS_KEY"]?.Trim() ?? "";

        // R2 has no folders. Keys are flat strings that the dashboard renders as though the part
        // before a "/" were one. Here it is a safety fence rather than organisation: everything
        // this class lists, offers and deletes is confined to one prefix, which is what lets
        // local testing run against the real bucket under "local-test/" with no way to touch a
        // production archive. Normalised rather than trusted, since without the trailing slash
        // the keys read "hockeypracticehockeypractice-...zip".
        var prefix = config["ARCHIVE_S3_PREFIX"]?.Trim() ?? "";
        _prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";

        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint,

            // R2 does not do virtual-host style addressing on the S3 endpoint, so the bucket has
            // to travel in the path.
            ForcePathStyle = true,

            // R2 has one region and expects this literal in the signature.
            AuthenticationRegion = "auto",

            // Set explicitly, and this is the important line. AWS turned on default request
            // checksums, and that default is the known source of failures against non-AWS
            // S3-compatible endpoints. Pinning the package version alone would not be enough:
            // leaving this at the SDK default means a future upgrade can silently change the
            // shape of every PUT this app makes. If an upload ever starts failing with a
            // checksum or signature error, start here.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }

    /// <summary>
    /// Whether all four values are present. The keys are never validated here, only their
    /// presence — a wrongly scoped or read-only token looks identical from outside and fails on
    /// the first upload with an explicit access denied, which is the honest place to find out.
    /// </summary>
    public static bool IsConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["ARCHIVE_S3_ENDPOINT"]) &&
        !string.IsNullOrWhiteSpace(config["ARCHIVE_S3_BUCKET"]) &&
        !string.IsNullOrWhiteSpace(config["ARCHIVE_S3_ACCESS_KEY_ID"]) &&
        !string.IsNullOrWhiteSpace(config["ARCHIVE_S3_SECRET_ACCESS_KEY"]);

    public string KeyFor(string name) => _prefix + name;

    /// <summary>
    /// Every archive under the prefix, newest first.
    ///
    /// Filtered on the archive name pattern here rather than at the one call site that deletes,
    /// so the guarantee holds for every caller: nothing this app has not written can be listed,
    /// offered for restore, or pruned. Anything else sharing the bucket is invisible to it.
    /// </summary>
    public async Task<IReadOnlyList<StoredBackup>> ListAsync(CancellationToken ct = default)
    {
        var found = new List<StoredBackup>();
        string? token = null;

        do
        {
            var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _prefix,
                ContinuationToken = token
            }, ct);

            foreach (var o in page.S3Objects)
            {
                var name = o.Key[_prefix.Length..];
                if (!VolumeBackupService.NamePattern.IsMatch(name)) continue;
                found.Add(new StoredBackup(o.Key, name, o.Size, o.LastModified.ToUniversalTime()));
            }

            token = page.IsTruncated ? page.NextContinuationToken : null;
        }
        while (token is not null);

        // By key, not by LastModified. The name carries the timestamp, so this is chronological
        // and it is deterministic in a way two objects written in the same second are not.
        return found.OrderByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    public async Task UploadAsync(string localPath, string key, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = localPath,
            ContentType = "application/zip",

            // R2 does not implement chunked payload signing. Measured, not guessed: the first
            // real upload from production failed with
            //   AmazonS3Exception: STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented
            // after the archive had been built successfully, so this is the last thing standing
            // between a working backup and no backup at all.
            //
            // With this off, the SDK hashes the whole file up front and sends one signed request
            // instead of a stream of signed chunks. That needs a seekable payload, which is why
            // the upload takes a FilePath rather than a stream. The cost is one extra read of the
            // file to compute the hash, which at archive sizes is irrelevant next to the upload.
            //
            // Kept alongside RequestChecksumCalculation in the client config: they are two
            // different AWS-isms that R2 rejects, and fixing only one still fails.
            UseChunkEncoding = false
        }, ct);

        _log.LogInformation("Uploaded {Key}", key);
    }

    public async Task DownloadAsync(string key, string localPath, CancellationToken ct = default)
    {
        using var response = await _client.GetObjectAsync(_bucket, key, ct);
        await response.WriteResponseStreamToFileAsync(localPath, append: false, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _client.DeleteObjectAsync(_bucket, key, ct);
        _log.LogWarning("Deleted archive {Key}", key);
    }

    public async Task<long?> SizeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, key, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
