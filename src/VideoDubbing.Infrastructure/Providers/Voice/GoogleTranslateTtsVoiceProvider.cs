using System.Text.RegularExpressions;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Voice;

/// <summary>
/// Key-free cloud TTS fallback using Google Translate's public <c>/translate_tts</c> endpoint.
/// Produces genuinely native-language speech (real French) for any supported language without
/// an API key or account. Designed strictly as a best-effort chain fallback: short segments
/// only, automatic pause between calls, and a clean error so the chain moves on if the
/// endpoint is unavailable or rate-limited.
/// </summary>
public sealed class GoogleTranslateTtsVoiceProvider : IVoiceProvider
{
    private const int MaxChars = 180;
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(600);
    private static readonly Regex MockPrefix = new(@"^\[\w{2}\]\s*", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public GoogleTranslateTtsVoiceProvider(HttpClient http) => _http = http;

    public string Name => "GoogleTts";

    public async Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        var clean = MockPrefix.Replace(text ?? string.Empty, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            throw new InvalidOperationException("Empty text segment.");
        }

        if (clean.Length > MaxChars)
        {
            throw new InvalidOperationException($"Segment too long for Google Translate TTS ({clean.Length} > {MaxChars} chars).");
        }

        var url = $"https://translate.google.com/translate_tts?ie=UTF-8&tl={Uri.EscapeDataString(language)}&client=tw-ob&total=1&idx=0&textlen={clean.Length}&q={Uri.EscapeDataString(clean)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://translate.google.com/");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google Translate TTS returned {(int)response.StatusCode}.");
        }

        await Task.Delay(PacingDelay, cancellationToken);
        await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(file, 1 << 20, cancellationToken);
    }
}