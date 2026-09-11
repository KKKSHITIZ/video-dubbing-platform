using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Speech;

/// <summary>
/// Fully offline, key-free speech-to-text using the Vosk local recognition engine and a small
/// downloadable English model. Transcribes the ORIGINAL audio of a video into real words so the
/// translate step translates actual speech instead of placeholder text.
/// The model is downloaded once (~40 MB) into <see cref="ProviderOptions.VoskModelPath"/> and
/// reused for all jobs.
/// </summary>
public sealed class VoskSpeechToTextProvider : ISpeechToTextProvider
{
    private const string ModelFolder = "vosk-model-small-en-us-0.15";
    private const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const string SpkModelFolder = "vosk-model-spk-0.4";
    private const string SpkModelUrl = "https://alphacephei.com/vosk/models/vosk-model-spk-0.4.zip";
    private const int SampleRate = 16000;

    private static readonly object ModelLock = new();
    private static Vosk.Model? _model;
    private static Vosk.SpkModel? _spkModel;

    private readonly HttpClient _http;
    private readonly ProviderOptions _providers;

    public VoskSpeechToTextProvider(HttpClient http, IOptions<ProviderOptions> providers)
    {
        _http = http;
        _providers = providers.Value;
    }

    public string Name => "Vosk";

    public async Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var modelDir = await EnsureModelAsync(ModelFolder, ModelUrl, "am/final.mdl", cancellationToken);
        var spkDir = await EnsureModelAsync(SpkModelFolder, SpkModelUrl, "final.ext.raw", cancellationToken);
        Vosk.Model model;
        Vosk.SpkModel? spkModel;
        lock (ModelLock)
        {
            _model ??= new Vosk.Model(modelDir);
            model = _model;
            _spkModel ??= new Vosk.SpkModel(spkDir);
            spkModel = _spkModel;
        }

        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken);
        var pcm = Normalize(PcmPayload(bytes));

        using var recognizer = new Vosk.VoskRecognizer(model, SampleRate, spkModel);
        recognizer.SetWords(true);
        var chunk = 1 << 15;
        var offset = 0;
        while (offset < pcm.Length)
        {
            var length = Math.Min(chunk, pcm.Length - offset);
            _ = recognizer.AcceptWaveform(pcm.Slice(offset, length).ToArray(), length);
            offset += length;
        }

        var finalJson = recognizer.FinalResult();
        var words = ParseWords(finalJson);

        var language = sourceLanguage is "auto" or "" ? "en" : sourceLanguage;
        var turnsOut = MapToTurns(words, turns);
        return (language, turnsOut);
    }

    private static List<(double Start, double End, string Text, float[] Spk)> ParseWords(string json)
    {
        var words = new List<(double, double, string, float[])>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in result.EnumerateArray())
                {
                    var start = word.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                    var end = word.TryGetProperty("end", out var e) ? e.GetDouble() : start;
                    var text = word.TryGetProperty("word", out var w) ? w.GetString() : string.Empty;
                    float[]? spk = null;
                    if (word.TryGetProperty("spk", out var spkEl) && spkEl.ValueKind == JsonValueKind.Array)
                    {
                        var emb = new List<float>();
                        foreach (var c in spkEl.EnumerateArray())
                        {
                            if (c.TryGetSingle(out var f))
                            {
                                emb.Add(f);
                            }
                        }

                        if (emb.Count > 0)
                        {
                            spk = emb.ToArray();
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        words.Add((start, end, text.Trim(), spk ?? Array.Empty<float>()));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Vosk result is still speech-gated downstream; keep words empty on parse failure.
        }

        return words;
    }

    /// <summary>
    /// True voice-based diarization: every word carries a Vosk speaker embedding (spk model).
    /// Words are clustered into (at most) 2 speakers by cosine similarity, then merged into
    /// non-overlapping timed turns. Falls back to the incoming silence-based turns when the
    /// speaker model produced no embeddings (single-distinct-voice audio).
    /// </summary>
    private IReadOnlyList<TranscriptTurn> MapToTurns(
        List<(double Start, double End, string Text, float[] Spk)> words,
        IReadOnlyList<SpeakerTurn> turns)
    {
        var embedded = words.Where(w => w.Spk.Length > 0).ToList();
        if (embedded.Count >= 4)
        {
            var clusterWords = ClusterBySpeaker(embedded);
            return TurnsFromClusters(clusterWords);
        }

        // No usable embeddings: fall back to silence-based turns from diarization.
        return MapToTurnsBySilenceTurns(words, turns);
    }

    private static List<(double Start, double End, string Text, int Cluster)> ClusterBySpeaker(
        List<(double Start, double End, string Text, float[] Spk)> words)
    {
        // Normalized speaker vectors.
        var vectors = words.Select(w => (Word: w, Vec: Normalize(w.Spk))).ToList();

        // Seed centroids with the two norm vectors farthest apart (or first vs farthest).
        var c0 = vectors[0].Vec;
        var c1 = vectors[^1].Vec;
        var bestDist = -1d;
        for (var i = 0; i < vectors.Count; i++)
        {
            for (var j = i + 1; j < vectors.Count; j++)
            {
                var d = Distance(vectors[i].Vec, vectors[j].Vec);
                if (d > bestDist)
                {
                    bestDist = d;
                    c0 = vectors[i].Vec;
                    c1 = vectors[j].Vec;
                }
            }
        }

        var assignments = new int[vectors.Count];
        for (var iter = 0; iter < 25; iter++)
        {
            var changed = false;
            double sum0x = 0, sum1x = 0;
            var sum0 = new double[c0.Length];
            var sum1 = new double[c1.Length];
            var n0 = 0;
            var n1 = 0;
            for (var i = 0; i < vectors.Count; i++)
            {
                var d0 = Distance(vectors[i].Vec, c0);
                var d1 = Distance(vectors[i].Vec, c1);
                var cluster = d0 <= d1 ? 0 : 1;
                if (cluster != assignments[i])
                {
                    changed = true;
                    assignments[i] = cluster;
                }

                if (cluster == 0)
                {
                    n0++;
                    sum0x += d0;
                    for (var k = 0; k < c0.Length; k++)
                    {
                        sum0[k] += vectors[i].Vec[k];
                    }
                }
                else
                {
                    n1++;
                    sum1x += d1;
                    for (var k = 0; k < c1.Length; k++)
                    {
                        sum1[k] += vectors[i].Vec[k];
                    }
                }
            }

            if (!changed)
            {
                break;
            }

            if (n0 > 0)
            {
                c0 = Normalize(sum0.Select(v => (float)(v / n0)).ToArray());
            }

            if (n1 > 0)
            {
                c1 = Normalize(sum1.Select(v => (float)(v / n1)).ToArray());
            }
        }

        // If the two centroids are nearly identical the audio is one speaker; collapse to a single cluster.
        if (Distance(c0, c1) < 0.30)
        {
            for (var i = 0; i < assignments.Length; i++)
            {
                assignments[i] = 0;
            }
        }

        return vectors
            .Select((v, i) => (v.Word.Start, v.Word.End, v.Word.Text, assignments[i]))
            .ToList();
    }

    private static IReadOnlyList<TranscriptTurn> TurnsFromClusters(List<(double Start, double End, string Text, int Cluster)> clusterWords)
    {
        // Merge consecutive words of the same speaker into utterance runs (gap &lt; 0.6s),
        // producing strictly non-overlapping, timestamped turns.
        var runs = new List<TranscriptTurn>();
        var ordered = clusterWords.OrderBy(w => w.Start).ToList();
        var runStart = ordered[0].Start;
        var runEnd = ordered[0].End;
        var cluster = ordered[0].Cluster;
        var builder = new StringBuilder(ordered[0].Text);
        for (var i = 1; i < ordered.Count; i++)
        {
            var word = ordered[i];
            if (word.Cluster == cluster && word.Start <= runEnd + 0.6)
            {
                runEnd = Math.Max(runEnd, word.End);
                builder.Append(' ').Append(word.Text);
            }
            else
            {
                runs.Add(new TranscriptTurn($"SPEAKER_{cluster:00}", runStart, runEnd, builder.ToString().Trim(), "en"));
                runStart = word.Start;
                runEnd = word.End;
                cluster = word.Cluster;
                builder.Clear();
                builder.Append(word.Text);
            }
        }

        runs.Add(new TranscriptTurn($"SPEAKER_{cluster:00}", runStart, runEnd, builder.ToString().Trim(), "en"));
        return runs;
    }

    private IReadOnlyList<TranscriptTurn> MapToTurnsBySilenceTurns(
        List<(double Start, double End, string Text, float[] Spk)> words,
        IReadOnlyList<SpeakerTurn> turns)
    {
        var buckets = new Dictionary<string, (double Start, double End, StringBuilder Text)>();
        var fallbackLabel = turns.FirstOrDefault()?.SpeakerLabel ?? "SPEAKER_00";

        foreach (var word in words)
        {
            var mid = (word.Start + word.End) / 2;
            var label = fallbackLabel;
            foreach (var turn in turns)
            {
                if (mid >= turn.StartSeconds - 0.05 && mid <= turn.EndSeconds + 0.05)
                {
                    label = turn.SpeakerLabel;
                    break;
                }
            }

            if (!buckets.TryGetValue(label, out var bucket))
            {
                bucket = (word.Start, word.End, new StringBuilder());
                buckets[label] = bucket;
            }

            if (bucket.Text.Length > 0)
            {
                bucket.Text.Append(' ');
            }

            bucket.Text.Append(word.Text);
            buckets[label] = (Math.Min(bucket.Start, word.Start), Math.Max(bucket.End, word.End), bucket.Text);
        }

        var result = new List<TranscriptTurn>();
        foreach (var (label, (start, end, text)) in buckets)
        {
            var trimmed = text.ToString().Trim();
            if (trimmed.Length > 0)
            {
                result.Add(new TranscriptTurn(label, Math.Round(start, 3), Math.Round(Math.Max(end, start + 0.1), 3), trimmed, "en"));
            }
        }

        if (result.Count == 0 && words.Count > 0)
        {
            result.Add(new TranscriptTurn(fallbackLabel, 0, Math.Max(words[^1].End, 0.1), string.Join(' ', words.Select(w => w.Text)), "en"));
        }

        return result;
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm <= 1e-9)
        {
            return vector;
        }

        return vector.Select(v => (float)(v / norm)).ToArray();
    }

    private static double Distance(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        var dot = 0d;
        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
        }

        // Cosine distance: 0 = identical direction, 2 = opposite.
        return 1.0 - dot;
    }

    private async Task<string> EnsureModelAsync(string folder, string url, string marker, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(_providers.VoskModelPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "data", "models")
            : _providers.VoskModelPath;
        var modelDir = Path.Combine(root, folder);
        if (Directory.Exists(modelDir) && File.Exists(Path.Combine(modelDir, marker)))
        {
            return modelDir;
        }

        lock (ModelLock)
        {
            if (Directory.Exists(modelDir) && File.Exists(Path.Combine(modelDir, marker)))
            {
                return modelDir;
            }
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, $"{folder}.zip");
        var candidates = new[] { url };
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await DownloadModelAsync(candidate, zipPath, cancellationToken);
                ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
                File.Delete(zipPath);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        if (!File.Exists(Path.Combine(modelDir, marker)))
        {
            throw new InvalidOperationException(
                $"Vosk model could not be downloaded to '{root}'. {last?.Message}")
            {
                Data = { ["ModelRoot"] = root }
            };
        }

        return modelDir;
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

    private static ReadOnlyMemory<byte> Normalize(ReadOnlyMemory<byte> pcm)
    {
        if (pcm.IsEmpty)
        {
            return pcm;
        }

        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = Math.Abs((int)BitConverter.ToInt16(pcm.Span.Slice(i, 2)));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        if (peak == 0 || peak >= 0.95 * short.MaxValue)
        {
            return pcm;
        }

        var gain = 0.89 * short.MaxValue / peak;
        var normalized = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var value = (int)Math.Round(BitConverter.ToInt16(pcm.Span.Slice(i, 2)) * gain);
            if (value > short.MaxValue)
            {
                value = short.MaxValue;
            }
            else if (value < short.MinValue)
            {
                value = short.MinValue;
            }

            normalized[i] = (byte)(value & 0xff);
            normalized[i + 1] = (byte)((value >> 8) & 0xff);
        }

        return normalized;
    }
}