using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Translation;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public string Name => "Mock";

    public Task<IReadOnlyList<string>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            request.Segments.Select(s => $"[{request.TargetLanguage}] {s.Text}").ToList());
}

/// <summary>
/// Context-aware LLM translation via OpenAI chat completions. The whole speaker-diarized transcript
/// (speaker + timing + text) is sent in one structured JSON request with a system prompt that
/// preserves tone/flow/speaker mapping and constrains length to each segment's timing window.
/// Output is aligned back to segments by echoed ids (survives duplicate utterances), and every
/// missing/empty item falls back to the source text so the pipeline can continue.
/// </summary>
public sealed class OpenAiTranslationProvider : ITranslationProvider
{
    private const int BatchSize = 30;

    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;

    public OpenAiTranslationProvider(HttpClient http, AiSecretsOptions secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string Name => "OpenAi";

    public async Task<IReadOnlyList<string>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_secrets.OpenAiApiKey))
        {
            throw new InvalidOperationException("OpenAI translation is not configured. Set AiSecrets:OpenAiApiKey.");
        }

        if (request.Segments.Count == 0)
        {
            return Array.Empty<string>();
        }

        var results = new string[request.Segments.Count];
        for (var offset = 0; offset < request.Segments.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = request.Segments.Skip(offset).Take(BatchSize).ToList();
            var payload = BuildPayload(batch, request.SourceLanguage, request.TargetLanguage, request.Theme);
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{_secrets.OpenAiBaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.OpenAiApiKey);
            using var response = await _http.SendAsync(requestMessage, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI translate failed with {(int)response.StatusCode}: {Truncate(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
            var batchResult = ParseTranslations(content, batch);
            for (var i = 0; i < batch.Count; i++)
            {
                results[offset + i] = i < batchResult.Count ? batchResult[i] : batch[i].Text;
            }
        }

        return results;
    }

    private static string BuildPayload(IReadOnlyList<TranslationSegment> segments, string sourceLanguage, string targetLanguage, string? theme)
    {
        var input = new
        {
            source_language = sourceLanguage,
            target_language = targetLanguage,
            segments = segments.Select((s, i) => new
            {
                id = $"S{i}",
                speaker = s.Speaker,
                start_s = Math.Round(s.StartSeconds, 2),
                end_s = Math.Round(s.EndSeconds, 2),
                text = s.Text
            }).ToList()
        };
        var system = string.IsNullOrWhiteSpace(theme) ? ContextAwareTranslationPrompt.System : ContextAwareTranslationPrompt.ForRemix(theme);
        return JsonSerializer.Serialize(new
        {
            model = "gpt-4o-mini",
            temperature = 0.3,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = JsonSerializer.Serialize(input) }
            }
        });
    }

    /// <summary>Parses the model output (JSON object with "translations", or a bare array) and aligns
    /// it back to the input batch by segment id. Any missing/empty entry falls back to the source text.</summary>
    public static IReadOnlyList<string> ParseTranslations(string content, IReadOnlyList<TranslationSegment> batch)
    {
        var cleaned = content.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = cleaned.IndexOf('\n');
            cleaned = newline >= 0 ? cleaned[(newline + 1)..] : cleaned[3..];
            cleaned = cleaned.Trim();
            var endFence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0)
            {
                cleaned = cleaned[..endFence].Trim();
            }
        }

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("translations", out var translations) &&
                translations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in translations.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    var text = item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String ? textEl.GetString() : null;
                    if (id is not null && text is not null)
                    {
                        byId[id] = text;
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    byIndex.Add(el.ValueKind == JsonValueKind.String ? el.GetString() ?? string.Empty : string.Empty);
                }
            }
        }
        catch (JsonException)
        {
            // fall through to source-text passthrough
        }

        var output = new List<string>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            var matched = byId.TryGetValue($"S{i}", out var byIdValue) ? byIdValue
                : i < byIndex.Count ? byIndex[i]
                : string.Empty;
            output.Add(string.IsNullOrWhiteSpace(matched) ? batch[i].Text : matched);
        }

        return output;
    }

    private static string Truncate(string value, int maxChars = 512)
        => value.Length <= maxChars ? value : value[..maxChars];
}

public sealed class GeminiTranslationProvider : ITranslationProvider
{
    public string Name => "Gemini";
    public Task<IReadOnlyList<string>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Gemini requires AiSecrets:GeminiApiKey.");
}

public sealed class ClaudeTranslationProvider : ITranslationProvider
{
    public string Name => "Claude";
    public Task<IReadOnlyList<string>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Claude requires AiSecrets:AnthropicApiKey.");
}

/// <summary>
/// Real DeepL translation client (free endpoint by default; switch AiSecrets:DeepLBaseUrl
/// to <c>https://api.deepl.com</c> for a Pro plan). Batches all segments in a single call,
/// preserving order so speaker-to-translation mapping stays exact.
/// </summary>
public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;

    public DeepLTranslationProvider(HttpClient http, AiSecretsOptions secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string Name => "DeepL";

    public async Task<IReadOnlyList<string>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_secrets.DeepLApiKey))
        {
            throw new InvalidOperationException("DeepL is not configured. Set AiSecrets:DeepLApiKey (https://www.deepl.com/pro-api).");
        }

        var texts = request.Segments.Select(s => s.Text).ToList();
        var requestBody = JsonSerializer.Serialize(new
        {
            text = texts,
            target_lang = DeepLLanguage(request.TargetLanguage),
            source_lang = DeepLLanguage(request.SourceLanguage)
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_secrets.DeepLBaseUrl.TrimEnd('/')}/v2/translate")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _secrets.DeepLApiKey);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepL translate failed with {(int)response.StatusCode}: {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        var translations = doc.RootElement.GetProperty("translations");
        if (translations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("DeepL returned an unexpected payload.");
        }

        var map = new Dictionary<string, string>();
        for (var i = 0; i < texts.Count; i++)
        {
            var translated = i < translations.GetArrayLength()
                ? translations[i].GetProperty("text").GetString()
                : null;
            map[texts[i]] = string.IsNullOrWhiteSpace(translated) ? texts[i] : translated;
        }

        return texts.Select(text => map.TryGetValue(text, out var value) ? value : text).ToList();
    }

    private static string DeepLLanguage(string language)
    {
        var normalized = language.Trim().Split('-')[0].ToUpperInvariant();
        if (normalized.Length == 2)
        {
            return normalized;
        }

        throw new InvalidOperationException($"Unsupported DeepL language code '{language}'.");
    }

    private static string Truncate(string value, int maxChars = 512)
        => value.Length <= maxChars ? value : value[..maxChars];
}
