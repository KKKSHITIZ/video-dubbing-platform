using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Speech;

public sealed class MockSpeechToTextProvider : ISpeechToTextProvider
{
    public string Name => "Mock";

    public Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var language = sourceLanguage is "auto" or "" ? "en" : sourceLanguage;
        var result = turns.Select((turn, i) => new TranscriptTurn(
            turn.SpeakerLabel,
            turn.StartSeconds,
            turn.EndSeconds,
            $"{turn.SpeakerLabel} speaking in segment {i + 1}.",
            language)).ToList();
        return Task.FromResult<(string, IReadOnlyList<TranscriptTurn>)>((language, result));
    }
}

public sealed class OpenAiWhisperSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;

    public OpenAiWhisperSpeechToTextProvider(HttpClient http, AiSecretsOptions secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string Name => "OpenAiWhisper";

    public async Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        EnsureKey(_secrets.OpenAiApiKey, Name);
        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(sourceLanguage), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_secrets.OpenAiBaseUrl.TrimEnd('/')}/audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.OpenAiApiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var language = doc.RootElement.TryGetProperty("language", out var lang) ? lang.GetString() ?? "en" : "en";
        var mapped = new List<TranscriptTurn>();
        if (doc.RootElement.TryGetProperty("segments", out var segments))
        {
            var i = 0;
            foreach (var segment in segments.EnumerateArray())
            {
                var start = segment.GetProperty("start").GetDouble();
                var end = segment.GetProperty("end").GetDouble();
                var text = segment.GetProperty("text").GetString() ?? string.Empty;
                var speaker = turns.ElementAtOrDefault(Math.Min(i, Math.Max(turns.Count - 1, 0)))?.SpeakerLabel ?? "SPEAKER_00";
                mapped.Add(new TranscriptTurn(speaker, start, end, text.Trim(), language));
                i++;
            }
        }

        if (mapped.Count == 0)
        {
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            mapped.Add(new TranscriptTurn("SPEAKER_00", 0, 1, text, language));
        }

        return (language, mapped);
    }

    internal static void EnsureKey(string key, string name)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"{name} is not configured. Set AiSecrets in configuration or user secrets.");
        }
    }
}

public sealed class DeepgramSpeechToTextProvider : ISpeechToTextProvider
{
    public string Name => "Deepgram";
    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;

    public DeepgramSpeechToTextProvider(HttpClient http, AiSecretsOptions secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public async Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        OpenAiWhisperSpeechToTextProvider.EnsureKey(_secrets.DeepgramApiKey, Name);
        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepgram.com/v1/listen?diarize=true&punctuate=true")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _secrets.DeepgramApiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var transcript = doc.RootElement.GetProperty("results").GetProperty("channels")[0].GetProperty("alternatives")[0].GetProperty("transcript").GetString() ?? string.Empty;
        var language = sourceLanguage == "auto" ? "en" : sourceLanguage;
        IReadOnlyList<TranscriptTurn> mapped = [new TranscriptTurn("SPEAKER_00", 0, turns.LastOrDefault()?.EndSeconds ?? 1, transcript, language)];
        return (language, mapped);
    }
}

public sealed class AssemblyAiSpeechToTextProvider : ISpeechToTextProvider
{
    public string Name => "AssemblyAI";
    public Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath, IReadOnlyList<SpeakerTurn> turns, string sourceLanguage, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("AssemblyAI requires AiSecrets:AssemblyAiApiKey. Use Mock or OpenAiWhisper for local development.");
    }
}

public sealed class GoogleSpeechToTextProvider : ISpeechToTextProvider
{
    public string Name => "GoogleSpeechToText";
    public Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath, IReadOnlyList<SpeakerTurn> turns, string sourceLanguage, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Google Speech-to-Text requires AiSecrets:GoogleSpeechApiKey.");
    }
}
