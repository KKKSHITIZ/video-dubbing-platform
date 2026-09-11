using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;

namespace VideoDubbing.Infrastructure.Localization;

/// <summary>
/// Minimal client for the ElevenLabs Music / SFX API (POST /v1/music). Generates an original
/// track from a text prompt with the given duration. The vendor returns the finished audio
/// stream, which is passed through untouched.
/// </summary>
public sealed class ElevenLabsMusicApi
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<ElevenLabsOptions> _elevenLabs;
    private readonly IOptions<AiSecretsOptions> _secrets;
    private readonly ILogger<ElevenLabsMusicApi> _logger;

    public ElevenLabsMusicApi(
        IHttpClientFactory httpFactory,
        IOptions<ElevenLabsOptions> elevenLabs,
        IOptions<AiSecretsOptions> secrets,
        ILogger<ElevenLabsMusicApi> logger)
    {
        _httpFactory = httpFactory;
        _elevenLabs = elevenLabs;
        _secrets = secrets;
        _logger = logger;
    }

    private string ResolveApiKey() => ElevenLabsDubbingService.ResolveApiKey(_elevenLabs.Value, _secrets.Value);

    public async Task<(Stream Stream, string ContentType, string FileName)> ComposeAsync(
        string prompt,
        int durationSeconds,
        string modelId,
        bool forceInstrumental,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A music prompt is required.", nameof(prompt));
        }

        durationSeconds = Math.Clamp(durationSeconds, 3, 600);
        var resolvedModel = string.IsNullOrWhiteSpace(modelId) ? "music_v2" : modelId.Trim();
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        var query = $"music_length_ms={durationSeconds * 1000}&model_id={Uri.EscapeDataString(resolvedModel)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/music?{query}")
        {
            Content = JsonContent.Create(new
            {
                prompt,
                force_instrumental = forceInstrumental
            })
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("ElevenLabs music compose failed {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            throw new InvalidOperationException($"ElevenLabs Music API rejected the request ({response.StatusCode}): {Truncate(body)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        return (stream, contentType, "elevenlabs-music.mp3");
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";
}