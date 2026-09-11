using System.Text.Json;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Translation;

/// <summary>
/// Key-free translation fallback using the MyMemory public API. Returns genuinely translated
/// text in any target language without an API key so the dub pipeline can fall back to real
/// translations whenever no paid provider key is configured. Designed as a best-effort chain
/// fallback: short texts, per-request pacing, and clean errors so the chain moves on.
/// </summary>
public sealed class MyMemoryTranslationProvider : ITranslationProvider
{
    private const int MaxChars = 500;
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(400);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public MyMemoryTranslationProvider(HttpClient http) => _http = http;

    public string Name => "MyMemory";

    public async Task<IReadOnlyList<string>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var texts = request.Segments.Select(s => s.Text).ToList();
        var map = new Dictionary<string, string>();
        foreach (var distinct in texts.Distinct())
        {
            if (string.IsNullOrWhiteSpace(distinct))
            {
                continue;
            }

            if (distinct.Length > MaxChars)
            {
                throw new InvalidOperationException($"Segment too long for MyMemory ({distinct.Length} > {MaxChars} chars).");
            }

            var langPair = $"{request.SourceLanguage}|{request.TargetLanguage}";
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(distinct)}&langpair={Uri.EscapeDataString(langPair)}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.UserAgent.ParseAdd("VideoDubbing/1.0");

            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"MyMemory returned {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var translated = ParseTranslatedText(json);
            if (string.IsNullOrWhiteSpace(translated))
            {
                throw new InvalidOperationException("MyMemory returned no translation.");
            }

            map[distinct] = translated;
            await Task.Delay(PacingDelay, cancellationToken);
        }

        return texts.Select(text => map.TryGetValue(text, out var value) ? value : text).ToList();
    }

    private static string ParseTranslatedText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("responseStatus", out var status) && status.TryGetInt32(out var code) && code != 200)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("responseData", out var data) &&
                data.TryGetProperty("translatedText", out var text))
            {
                return text.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}