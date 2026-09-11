namespace VideoDubbing.Application.Storage;

public interface IObjectStorage
{
    Task<string> SaveAsync(Stream content, string key, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task<byte[]> ReadAllBytesAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    string Combine(params string[] parts);
}
