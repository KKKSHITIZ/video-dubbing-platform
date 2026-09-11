using System.Text.Json;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Translation;

/// <summary>
/// Key-free translation fallback using Google's public <c>translate_a/single</c> (gtx) endpoint.
/// Returns genuinely translated text in any target language without an API key or account so the
/// dub pipeline can fall back to real translations when no paid provider key is configured.
/// Designed as a best-effort chain fallback: short texts, clean errors on failure so the chain
/// moves on to the next provider.
/// </summary>
public sealed class GoogleGenXTranslationProvider : ITranslationProvider
{
    private const int MaxChars = 3500;
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(350);
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true };

    private readonly HttpClient _http;

    public GoogleGenXTranslationProvider(HttpClient http) => _http = http;

    public string Name => "GoogleGtx";

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
                throw new InvalidOperationException($"Segment too long for GoogleGtx ({distinct.Length} > {MaxChars} chars).");
            }

            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(request.SourceLanguage)}&tl={Uri.EscapeDataString(request.TargetLanguage)}&dt=t&q={Uri.EscapeDataString(distinct)}";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"GoogleGtx returned {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var translated = ParseTranslatedText(json);
            if (string.IsNullOrWhiteSpace(translated))
            {
                throw new InvalidOperationException("GoogleGtx returned no translation.");
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
            using var doc = JsonDocument.Parse(json, JsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var rows = doc.RootElement[0];
            if (rows.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > 0)
                {
                    var chunk = row[0].GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        parts.Add(chunk);
                    }
                }
            }

            return string.Concat(parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}