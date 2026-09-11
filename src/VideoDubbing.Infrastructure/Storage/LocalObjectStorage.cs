using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Storage;

namespace VideoDubbing.Infrastructure.Storage;

public sealed class LocalObjectStorage : IObjectStorage
{
    private readonly string _root;

    public LocalObjectStorage(StorageOptions options)
    {
        _root = options.LocalRoot;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(Stream content, string key, string contentType, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        Stream stream = File.OpenRead(Resolve(key));
        return Task.FromResult(stream);
    }

    public Task<byte[]> ReadAllBytesAsync(string key, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Resolve(key), cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string Combine(params string[] parts) => Path.Combine(parts).Replace('\\', '/');

    private string Resolve(string key)
    {
        var sanitized = key.Replace('\\', '/').TrimStart('/');
        if (sanitized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid storage key.");
        }

        return Path.GetFullPath(Path.Combine(_root, sanitized.Replace('/', Path.DirectorySeparatorChar)));
    }
}
