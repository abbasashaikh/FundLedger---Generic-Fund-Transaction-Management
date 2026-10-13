using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FundLedger.Application.Attachments;
using Microsoft.Extensions.Configuration;

namespace FundLedger.Infrastructure.Storage;

/// <summary>
/// <c>Storage:Provider</c> selects the store: <c>S3</c> (Cloudflare R2 or any S3-compatible private bucket) for staging and
/// production, <c>Local</c> (a folder; development and tests), or empty/<c>None</c> (attachments off: uploads answer 503).
/// S3 secrets come from the environment (<c>Storage__S3__SecretAccessKey</c>), never from files in the repository.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string Provider { get; set; } = string.Empty;

    public string LocalPath { get; set; } = string.Empty;

    public S3Options S3 { get; set; } = new();

    public static StorageOptions From(IConfiguration configuration)
    {
        var o = new StorageOptions();
        configuration.GetSection(Section).Bind(o);
        return o;
    }
}

public sealed class S3Options
{
    /// <summary>For R2: <c>https://&lt;account-id&gt;.r2.cloudflarestorage.com</c>.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>"auto" for R2. Backblaze B2 and most others need the real region name (for B2 it is in the endpoint: s3.<b>us-west-004</b>.backblazeb2.com).</summary>
    public string Region { get; set; } = "auto";
}

/// <summary>Attachments switched off: nothing is stored, nothing is promised.</summary>
public sealed class NoObjectStorage : IObjectStorage
{
    public bool IsConfigured => false;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct) => throw new InvalidOperationException("No object storage is configured.");

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(null);

    public Uri? CreateDownloadUrl(string key, TimeSpan lifetime, string fileName, string contentType) => null;
}

/// <summary>Files in a folder. For development and tests; the API streams the bytes, since there is no direct link to give.</summary>
public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;

    public LocalObjectStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public bool IsConfigured => true;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true) : null);
    }

    public Uri? CreateDownloadUrl(string key, TimeSpan lifetime, string fileName, string contentType) => null;

    /// <summary>Keys are made by the service, but a key that climbs out of the folder is refused anyway.</summary>
    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new InvalidOperationException("Invalid storage key.");
    }
}

/// <summary>A private S3-compatible bucket (Cloudflare R2). Objects are never public; downloads use a link that expires in 60 s (TR-053).</summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3ObjectStorage(S3Options o)
    {
        _bucket = o.Bucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(o.AccessKeyId, o.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = o.ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = string.IsNullOrWhiteSpace(o.Region) ? "auto" : o.Region,
            // R2 does not take the SDK's default CRC32 trailers on uploads.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public bool IsConfigured => true;

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = stream, ContentType = contentType, DisablePayloadSigning = false }, ct)
            .ConfigureAwait(false);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, key, ct).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Uri? CreateDownloadUrl(string key, TimeSpan lifetime, string fileName, string contentType)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(lifetime), Protocol = Protocol.HTTPS,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = contentType,
                ContentDisposition = $"inline; filename=\"{fileName.Replace("\"", string.Empty, StringComparison.Ordinal)}\"",
            },
        };
        return new Uri(_client.GetPreSignedURL(request));
    }

    public void Dispose() => _client.Dispose();
}
