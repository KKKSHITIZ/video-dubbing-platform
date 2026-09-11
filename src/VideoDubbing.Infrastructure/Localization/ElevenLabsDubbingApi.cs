using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;

namespace VideoDubbing.Infrastructure.Localization;

/// <summary>A named HttpClient used for all ElevenLabs-only APIs (Dubbing, Sound Effects, Music).</summary>
public static class ElevenLabsDubbingService
{
    public const string HttpClientName = "ElevenLabsClient";

    private const string PlaceholderKey = "YOUR_ELEVENLABS_KEY";

    /// <summary>Resolves the xi-api-key from ElevenLabs:ApiKey or AiSecrets:ElevenLabsApiKey, ignoring the docs placeholder.</summary>
    public static string ResolveApiKey(ElevenLabsOptions elevenLabs, AiSecretsOptions secrets)
    {
        foreach (var candidate in new[] { elevenLabs.ApiKey?.Trim(), secrets.ElevenLabsApiKey?.Trim() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals(PlaceholderKey, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "ElevenLabs API key is not configured. Set 'ElevenLabs:ApiKey' (or user-secret 'AiSecrets:ElevenLabsApiKey') in appsettings.json / user-secrets, then restart the API.");
    }
}

public sealed record ElevenLabsDubbingResult(string DubbingId, string? Error = null);

public sealed record ElevenLabsDubbingStatus(
    string DubbingId,
    string Status,
    string? Error,
    IReadOnlyList<string>? TargetLanguages);

public sealed record ElevenLabsTranscriptSegment(
    string Speaker,
    string Text,
    double StartSeconds,
    double EndSeconds);

/// <summary>
/// Minimal client for the ElevenLabs Dubbing product (POST /v1/dubbing). The vendor engine runs
/// transcription, translation, synthesis and mixing server-side and returns a single target-language
/// audio track whose duration matches the source video, so the produced dubbing keeps the original
/// pace and audio slot.
/// </summary>
public sealed partial class ElevenLabsDubbingApi
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<ElevenLabsOptions> _elevenLabs;
    private readonly IOptions<AiSecretsOptions> _secrets;
    private readonly ILogger<ElevenLabsDubbingApi> _logger;

    public ElevenLabsDubbingApi(
        IHttpClientFactory httpFactory,
        IOptions<ElevenLabsOptions> elevenLabs,
        IOptions<AiSecretsOptions> secrets,
        ILogger<ElevenLabsDubbingApi> logger)
    {
        _httpFactory = httpFactory;
        _elevenLabs = elevenLabs;
        _secrets = secrets;
        _logger = logger;
    }

    private string ResolveApiKey() => ElevenLabsDubbingService.ResolveApiKey(_elevenLabs.Value, _secrets.Value);

    public async Task<ElevenLabsDubbingResult> CreateAsync(
        Stream content,
        string fileName,
        string contentType,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "video/mp4" : contentType);
        form.Add(fileContent, "file", Path.GetFileName(fileName));
        form.Add(new StringContent(targetLanguage), "target_lang");
        if (!string.IsNullOrWhiteSpace(sourceLanguage) && !sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(sourceLanguage), "source_lang");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/dubbing")
        {
            Content = form
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ElevenLabs create dubbing failed {Status}: {Body}", (int)response.StatusCode, Truncate(body));
            throw new InvalidOperationException($"ElevenLabs Dubbing API rejected the request ({response.StatusCode}): {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var dubbingId = doc.RootElement.TryGetProperty("dubbing_id", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(dubbingId))
        {
            throw new InvalidOperationException($"ElevenLabs did not return a dubbing_id. Body: {Truncate(body)}");
        }

        _logger.LogInformation("ElevenLabs dubbing submitted {Id} for {Source}->{Target}", dubbingId, sourceLanguage, targetLanguage);
        return new ElevenLabsDubbingResult(dubbingId);
    }

    public async Task<ElevenLabsDubbingStatus> GetStatusAsync(string dubbingId, CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        using var response = await http.GetAsync($"/v1/dubbing/{dubbingId}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ElevenLabs status lookup failed ({response.StatusCode}): {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var languages = new List<string>();
        if (root.TryGetProperty("target_languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lang in langs.EnumerateArray())
            {
                if (lang.TryGetProperty("language_code", out var code))
                {
                    languages.Add(code.GetString() ?? string.Empty);
                }
                else
                {
                    languages.Add(lang.GetString() ?? string.Empty);
                }
            }
        }

        return new ElevenLabsDubbingStatus(dubbingId, status, error, languages);
    }

    public async Task<(Stream Stream, string ContentType, string FileName)> DownloadAudioAsync(
        string dubbingId,
        string language,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        var response = await http.GetAsync($"/v1/dubbing/{dubbingId}/audio/{language}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ElevenLabs audio download failed ({response.StatusCode}): {Truncate(body)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
        return (stream, contentType, $"dubbed-{language}.mp3");
    }

    public async Task<string> DownloadTranscriptAsync(string dubbingId, string language, CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        var response = await http.GetAsync($"/v1/dubbing/{dubbingId}/transcript/{language}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ElevenLabs transcript download failed ({response.StatusCode}): {Truncate(body)}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Downloads the vendor transcript JSON and normalizes it into speaker-wise segments.</summary>
    public async Task<IReadOnlyList<ElevenLabsTranscriptSegment>> DownloadTranscriptSegmentsAsync(
        string dubbingId,
        string language,
        CancellationToken cancellationToken)
    {
        var json = await DownloadTranscriptAsync(dubbingId, language, cancellationToken);
        return ParseTranscriptJson(json);
    }

    public async Task DeleteAsync(string dubbingId, CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        var http = _httpFactory.CreateClient(ElevenLabsDubbingService.HttpClientName);
        http.DefaultRequestHeaders.Remove("xi-api-key");
        http.DefaultRequestHeaders.Add("xi-api-key", apiKey);

        using var response = await http.DeleteAsync($"/v1/dubbing/{dubbingId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ElevenLabs delete failed ({response.StatusCode}): {Truncate(body)}");
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    /// <summary>
    /// Normalizes either ElevenLabs transcript schema into speaker-wise segments.
    /// New format: { "transcript": [ { "speaker_id", "translated_text", "start", "end" } ] }
    /// Legacy format: { "paragraphs": [ { "speaker", "sentences": [ { "text", "start_time", "end_time" } ] } ] }
    /// </summary>
    public static IReadOnlyList<ElevenLabsTranscriptSegment> ParseTranscriptJson(string json)
    {
        var result = new List<ElevenLabsTranscriptSegment>();
        JsonDocument? parsed = null;
        JsonElement root;
        try
        {
            parsed = JsonDocument.Parse(json);
            root = parsed.RootElement;
        }
        catch (JsonException)
        {
            parsed?.Dispose();
            return result;
        }

        using (parsed)
        {
        if (root.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in transcript.EnumerateArray())
            {
                var speaker = item.TryGetProperty("speaker_id", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? "SPEAKER_00"
                    : "SPEAKER_00";
                var text = item.TryGetProperty("translated_text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? string.Empty
                    : string.Empty;
                var start = GetSeconds(item, "start");
                var end = GetSeconds(item, "end");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(new ElevenLabsTranscriptSegment(speaker, text.Trim(), start, end));
                }
            }

            return result;
        }

        if (root.TryGetProperty("paragraphs", out var paragraphs) && paragraphs.ValueKind == JsonValueKind.Array)
        {
            foreach (var paragraph in paragraphs.EnumerateArray())
            {
                var speaker = paragraph.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String
                    ? sp.GetString() ?? "SPEAKER_00"
                    : "SPEAKER_00";
                if (!paragraph.TryGetProperty("sentences", out var sentences) || sentences.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var sentence in sentences.EnumerateArray())
                {
                    var text = sentence.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String
                        ? tt.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var start = GetSeconds(sentence, "start_time", "start_time_secs");
                    var end = GetSeconds(sentence, "end_time", "end_time_secs");
                    result.Add(new ElevenLabsTranscriptSegment(speaker, text.Trim(), start, end));
                }
            }

            return result;
        }

        return result;
        }
    }

    private static double GetSeconds(JsonElement element, params string[] names) =>
        names.Select(n => element.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.Number || v.ValueKind == JsonValueKind.String)
                ? ParseSeconds(v)
                : double.NaN)
            .Where(v => !double.IsNaN(v))
            .DefaultIfEmpty(0)
            .First();

    private static double ParseSeconds(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var d))
        {
            return d;
        }

        return double.NaN;
    }
}