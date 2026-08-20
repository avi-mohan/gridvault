using Amazon.S3;
using Amazon.S3.Model;

namespace GridVault.Ingestion.ObjectStorage;

public sealed class RawPayloadStoreOptions
{
    public required string ServiceUrl { get; init; }
    public required string Bucket { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
}

/// <summary>
/// Immutable landing zone for raw upstream payloads (MinIO locally, any
/// S3-compatible store in general). The raw landing step is
/// non-negotiable — see CLAUDE.md — so PutAsync refuses to land over an
/// existing key rather than silently overwrite it.
/// </summary>
public sealed class RawPayloadStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public RawPayloadStore(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public static IAmazonS3 CreateClient(RawPayloadStoreOptions options) => new AmazonS3Client(
        options.AccessKey,
        options.SecretKey,
        new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl,
            ForcePathStyle = true,
        });

    public async Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _s3.ListBucketsAsync(cancellationToken);
        if (!response.Buckets.Exists(bucket => bucket.BucketName == _bucket))
        {
            await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
        }
    }

    public async Task PutAsync(RawPayloadKey key, byte[] payload, CancellationToken cancellationToken = default)
    {
        var formattedKey = key.Format();

        if (await ExistsAsync(formattedKey, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Raw payload '{formattedKey}' already exists in object storage; landing must not overwrite it.");
        }

        using var stream = new MemoryStream(payload);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = formattedKey,
            InputStream = stream,
        }, cancellationToken);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var response = await _s3.GetObjectAsync(_bucket, key, cancellationToken);
        using var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
