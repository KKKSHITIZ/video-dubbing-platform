using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using VideoDubbing.Application.Media;

namespace VideoDubbing.IntegrationTests;

/// <summary>
/// Boots the real API host with an in-memory database, the no-op job queue
/// and a media-processor stand-in so tests require no external services.
/// </summary>
public sealed class DubbingWebApplicationFactory : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(
        Path.GetTempPath(), "video-dubbing-it", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(StorageRoot);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(StorageRoot, "videodubbing.db")}");
        builder.UseSetting("Messaging:Provider", "None");
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting("Storage:LocalRoot", StorageRoot);
        builder.UseSetting("Security:EnableRateLimiting", "false");
        builder.UseSetting("Processing:MaxUploadSizeMb", "10");
        builder.UseSetting("Processing:MaxDurationMinutes", "10");
        builder.UseSetting("Providers:EnableFallback", "false");
        builder.UseSetting("Providers:EnableCostAwareRouting", "false");
        builder.UseSetting("Providers:SpeechToText:Primary", "Mock");
        builder.UseSetting("Providers:Translation:Primary", "Mock");
        builder.UseSetting("Providers:Translation:Fallbacks", "[]");
        builder.UseSetting("Providers:Voice:Primary", "LocalFfmpeg");
        builder.UseSetting("Providers:Voice:Fallbacks", "[]");

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IMediaProcessor>(_ => new TestMediaProcessor());
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (Directory.Exists(StorageRoot))
                {
                    Directory.Delete(StorageRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        base.Dispose(disposing);
    }
}
