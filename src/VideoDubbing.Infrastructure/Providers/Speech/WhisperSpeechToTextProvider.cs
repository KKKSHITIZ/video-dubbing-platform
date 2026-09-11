using System.Text;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using Whisper.net;

namespace VideoDubbing.Infrastructure.Providers.Speech;

/// <summary>
/// Fully offline, key-free speech-to-text built on Whisper.net (whisper.cpp backend). Much more
/// accurate than Vosk and it can detect the spoken language itself via the multilingual ggml model.
/// The model (default <c>ggml-base.bin</c>, ~140 MB) is downloaded once into
/// <see cref="ProviderOptions.VoskModelPath"/>/<see cref="ProviderOptions.WhisperModelPath"/>
/// and reused for every job. Speaker labels come from the diarization turns: each whisper segment
/// is assigned to the turn it overlaps most with.
/// </summary>
public sealed class WhisperSpeechToTextProvider : ISpeechToTextProvider
{
    private const string DefaultModel = "ggml-base.bin";
    private const string ModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static readonly object ModelLock = new();
    private static WhisperFactory? _factory;

    private readonly HttpClient _http;
    private readonly ProviderOptions _providers;

    public WhisperSpeechToTextProvider(HttpClient http, IOptions<ProviderOptions> providers)
    {
        _http = http;
        _providers = providers.Value;
    }

    public string Name => "Whisper";

    public async Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var modelPath = await EnsureModelAsync(cancellationToken);

        WhisperFactory factory;
        lock (ModelLock)
        {
            _factory ??= WhisperFactory.FromPath(modelPath);
            factory = _factory;
        }

        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        var samples = ToMonoFloat(PcmPayload(bytes));

        var builder = factory.CreateBuilder()
            .WithThreads(4)
            .WithTokenTimestamps();

        var languageHint = sourceLanguage?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(languageHint) || languageHint is "auto" or "detect")
        {
            builder = builder.WithLanguageDetection();
        }
        else if (IsValidLanguageCode(languageHint))
        {
            builder = builder.WithLanguage(languageHint);
        }

        using var processor = builder.Build();

        var segments = new List<SegmentData>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            segments.Add(segment);
        }

        var detected = segments.Select(s => s.Language).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        var language = detected ?? (IsValidLanguageCode(languageHint) ? languageHint : "en");
        var turnsOut = MapToTurns(segments, turns, language);
        return (language, turnsOut);
    }

    /// <summary>
    /// Merges whisper segments into timed turns, labelling each segment with the diarization turn it
    /// overlaps most with. Segments that match no turn collapse into one remaining utterance.
    /// </summary>
    private static IReadOnlyList<TranscriptTurn> MapToTurns(
        List<SegmentData> segments,
        IReadOnlyList<SpeakerTurn> turns,
        string language)
    {
        var result = new List<TranscriptTurn>();
        var fallbackLabel = turns.FirstOrDefault()?.SpeakerLabel ?? "SPEAKER_00";
        var unmatched = new List<SegmentData>();
        var pending = new (string Label, double Start, double End, StringBuilder Text)?();

        void Flush()
        {
            if (pending is { } p && p.Text.Length > 0)
            {
                result.Add(new TranscriptTurn(p.Label, Math.Round(p.Start, 3), Math.Round(Math.Max(p.End, p.Start + 0.1), 3), p.Text.ToString().Trim(), language));
            }

            pending = null;
        }

        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            var label = ResolveSpeaker(segment, turns, fallbackLabel);
            var start = segment.Start.TotalSeconds;
            var end = segment.End.TotalSeconds;

            if (pending is { } p && p.Label == label && start <= p.End + 0.6)
            {
                pending = (label, Math.Min(p.Start, start), Math.Max(p.End, end), p.Text.Append(' ').Append(text));
            }
            else
            {
                Flush();
                pending = (label, start, end, new StringBuilder(text));
            }
        }

        Flush();

        if (result.Count == 0 && segments.Count > 0)
        {
            result.Add(new TranscriptTurn(
                fallbackLabel,
                0,
                Math.Max(segments[^1].End.TotalSeconds, 0.1),
                string.Join(' ', segments.Select(s => s.Text?.Trim()).Where(t => !string.IsNullOrWhiteSpace(t))),
                language));
        }

        return result;
    }

    private static string ResolveSpeaker(SegmentData segment, IReadOnlyList<SpeakerTurn> turns, string fallback)
    {
        var mid = (segment.Start.TotalSeconds + segment.End.TotalSeconds) / 2;
        if (turns.Count == 0)
        {
            return fallback;
        }

        string? best = null;
        var bestOverlap = 0d;
        foreach (var turn in turns)
        {
            var overlap = Math.Min(segment.End.TotalSeconds, turn.EndSeconds) - Math.Max(segment.Start.TotalSeconds, turn.StartSeconds);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = turn.SpeakerLabel;
            }
        }

        return best ?? fallback;
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(_providers.WhisperModel) ? DefaultModel : _providers.WhisperModel.Trim();
        var root = RootModelPath();
        Directory.CreateDirectory(root);
        var modelPath = Path.Combine(root, name);

        if (File.Exists(modelPath))
        {
            return modelPath;
        }

        lock (ModelLock)
        {
            if (File.Exists(modelPath))
            {
                return modelPath;
            }
        }

        var download = new FileInfo($"{modelPath}.tmp");
        var candidates = new[] { ModelBaseUrl + Uri.EscapeDataString(name) };
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                if (download.Exists)
                {
                    download.Delete();
                }

                await DownloadModelAsync(candidate, download.FullName, cancellationToken);
                File.Move(download.FullName, modelPath, overwrite: true);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                try
                {
                    download.Delete();
                }
                catch (IOException)
                {
                }
            }
        }

        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException(
                $"Whisper model '{name}' could not be downloaded to '{root}'. {last?.Message}")
            {
                Data = { ["ModelRoot"] = root }
            };
        }

        return modelPath;
    }

    private string RootModelPath()
    {
        if (!string.IsNullOrWhiteSpace(_providers.WhisperModelPath))
        {
            return _providers.WhisperModelPath;
        }

        return string.IsNullOrWhiteSpace(_providers.VoskModelPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "data", "models")
            : _providers.VoskModelPath;
    }

    private async Task DownloadModelAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VideoDubbing/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool IsValidLanguageCode(string? code) =>
        code is { Length: 2 } && code.All(char.IsAsciiLetterLower);

    private static ReadOnlyMemory<byte> PcmPayload(byte[] file)
    {
        if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(file, 8, 4) != "WAVE")
        {
            return file;
        }

        var pos = 12;
        while (pos + 8 <= file.Length)
        {
            var id = Encoding.ASCII.GetString(file, pos, 4);
            var size = BitConverter.ToInt32(file, pos + 4);
            if (id == "data")
            {
                var payload = Math.Min(size, file.Length - pos - 8);
                return payload > 0 ? file.AsMemory(pos + 8, payload) : ReadOnlyMemory<byte>.Empty;
            }

            pos += 8 + size + (size % 2);
        }

        return file;
    }

    private static float[] ToMonoFloat(ReadOnlyMemory<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm.Span.Slice(i * 2, 2)) / 32768f;
        }

        return samples;
    }
}