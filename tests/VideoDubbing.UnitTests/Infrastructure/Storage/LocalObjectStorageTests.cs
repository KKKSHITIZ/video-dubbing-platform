using FluentAssertions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Infrastructure.Storage;

namespace VideoDubbing.UnitTests.Infrastructure.Storage;

public sealed class LocalObjectStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-dubbing-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalObjectStorage _storage;

    public LocalObjectStorageTests()
    {
        _storage = new LocalObjectStorage(new StorageOptions { LocalRoot = _root });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task Save_then_read_roundtrips_bytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await using var input = new MemoryStream(bytes);

        await _storage.SaveAsync(input, "jobs/abc/audio.wav", "audio/wav", CancellationToken.None);
        var content = await _storage.ReadAllBytesAsync("jobs/abc/audio.wav", CancellationToken.None);

        content.Should().Equal(bytes);
    }

    [Fact]
    public async Task Save_creates_nested_directories()
    {
        await using var input = new MemoryStream([9, 8, 7]);
        var saved = await _storage.SaveAsync(input, "a/b/c/file.mp4", "video/mp4", CancellationToken.None);

        saved.Should().Be("a/b/c/file.mp4");
        File.Exists(Path.Combine(_root, "a", "b", "c", "file.mp4")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_removes_file()
    {
        await using var input = new MemoryStream([1]);
        await _storage.SaveAsync(input, "x/y.mp4", "video/mp4", CancellationToken.None);

        await _storage.DeleteAsync("x/y.mp4", CancellationToken.None);

        File.Exists(Path.Combine(_root, "x", "y.mp4")).Should().BeFalse();
    }

    [Fact]
    public async Task Opening_missing_key_throws()
    {
        Func<Task> act = () => _storage.OpenReadAsync("does/not/exist.mp4", CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../secret")]
    [InlineData("..\\secret\\file")]
    public async Task Traversal_keys_are_rejected(string key)
    {
        Func<Task> act = () => _storage.OpenReadAsync(key, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Invalid storage key*");
    }

    [Fact]
    public void Combine_uses_forward_slashes()
    {
        _storage.Combine("jobs", Guid.NewGuid().ToString("N"), "source.mp4").Should().NotContain("\\");
    }
}
