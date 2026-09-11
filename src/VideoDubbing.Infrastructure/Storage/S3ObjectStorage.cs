using Amazon.S3;
using Amazon.S3.Model;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Storage;

namespace VideoDubbing.Infrastructure.Storage;

public sealed class S3ObjectStorage : IObjectStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3ObjectStorage(IAmazonS3 client, StorageOptions options)
    {
        _client = client;
        _bucket = options.S3.Bucket;
    }

    public async Task<string> SaveAsync(Stream content, string key, string contentType, CancellationToken cancellationToken)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType
        }, cancellationToken);
        return key;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
        return response.ResponseStream;
    }

    public async Task<byte[]> ReadAllBytesAsync(string key, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(key, cancellationToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken);
    }

    public string Combine(params string[] parts) => string.Join('/', parts);
}
