using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;

namespace VideoDubbing.Infrastructure.Media;

public sealed class FfmpegMediaProcessor : IMediaProcessor
{
    private readonly ILogger<FfmpegMediaProcessor> _logger;

    public FfmpegMediaProcessor(IOptions<MediaOptions> options, ILogger<FfmpegMediaProcessor> logger)
    {
        _logger = logger;
        FfmpegPath = FfmpegLocator.Resolve(options.Value.FfmpegPath);
        FfprobePath = FfmpegLocator.Resolve(options.Value.FfprobePath);
    }

    private string FfmpegPath { get; }
    private string FfprobePath { get; }

    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var json = await RunAsync(FfprobePath,
            $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
            cancellationToken);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var duration = 0d;
        if (doc.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationEl) &&
            double.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            duration = parsed;
        }

        var formatName = format.TryGetProperty("format_name", out var nameEl) ? nameEl.GetString() ?? "unknown" : "unknown";
        int? width = null;
        int? height = null;
        var hasAudio = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : null;
                if (codecType == "video")
                {
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : null;
                }

                if (codecType == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        return new MediaProbeResult(duration, formatName, width, height, hasAudio);
    }

    public Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken cancellationToken) =>
        RunAsync(FfmpegPath,
            $"-y -i \"{videoPath}\" -vn -ac 1 -ar 16000 \"{audioPath}\"",
            cancellationToken,
            capture: false);

    public async Task<IReadOnlyList<SpeakerTurn>> DetectSilenceTurnsAsync(string audioPath, CancellationToken cancellationToken)
    {
        var output = await RunAsync(FfmpegPath,
            $"-i \"{audioPath}\" -af silencedetect=noise=-30dB:d=0.4 -f null -",
            cancellationToken,
            fromStdErr: true);

        var silenceStarts = new List<double>();
        var silenceEnds = new List<double>();
        foreach (var line in output.Split('\n'))
        {
            if (line.Contains("silence_start:", StringComparison.OrdinalIgnoreCase))
            {
                silenceStarts.Add(ParseAfter(line, "silence_start:"));
            }
            else if (line.Contains("silence_end:", StringComparison.OrdinalIgnoreCase))
            {
                silenceEnds.Add(ParseAfter(line, "silence_end:"));
            }
        }

        var probe = await ProbeAsync(audioPath, cancellationToken);
        var duration = probe.DurationSeconds;
        var turns = new List<SpeakerTurn>();
        var cursor = 0d;
        var speaker = 0;
        var index = 0;
        while (cursor < duration - 0.15)
        {
            var nextSilence = index < silenceStarts.Count ? silenceStarts[index] : duration;
            if (nextSilence - cursor >= 0.25)
            {
                var label = $"SPEAKER_{speaker % 2:00}";
                turns.Add(new SpeakerTurn(label, Math.Round(cursor, 3), Math.Round(Math.Min(nextSilence, duration), 3)));
                speaker++;
            }

            cursor = index < silenceEnds.Count ? Math.Max(silenceEnds[index], cursor) : duration;
            index++;
        }

        if (turns.Count == 0)
        {
            turns.Add(new SpeakerTurn("SPEAKER_00", 0, Math.Max(duration, 1)));
        }

        return turns;
    }

    public Task GenerateToneAsync(string outputPath, double durationSeconds, int frequencyHz, CancellationToken cancellationToken) =>
        RunAsync(FfmpegPath,
            $"-y -f lavfi -i \"sine=frequency={frequencyHz}:duration={durationSeconds.ToString(CultureInfo.InvariantCulture)}\" -ar 16000 -ac 1 \"{outputPath}\"",
            cancellationToken,
            capture: false);

    public async Task ExtractAudioSegmentAsync(string audioPath, double startSeconds, double endSeconds, string outputPath, CancellationToken cancellationToken)
    {
        var start = startSeconds.ToString(CultureInfo.InvariantCulture);
        var dur = Math.Max(0.1, endSeconds - startSeconds).ToString(CultureInfo.InvariantCulture);
        await RunAsync(FfmpegPath,
            $"-y -ss {start} -t {dur} -i \"{audioPath}\" -ar 16000 -ac 1 \"{outputPath}\"",
            cancellationToken,
            capture: false);
    }

    public async Task<string> TimeStretchAsync(string audioPath, double targetEndSeconds, double targetStartSeconds, string outputPath, CancellationToken cancellationToken)
    {
        var inputDuration = await ProbeDurationAsync(audioPath, cancellationToken);
        var targetDuration = Math.Max(0.05, targetEndSeconds - targetStartSeconds);
        var ratio = inputDuration <= 0 ? 1.0 : inputDuration / targetDuration;
        // Keep the dubbed speech at roughly the original pace: never slow it below ~1.33x
        // its natural duration and never rush it beyond 1.6x. Extreme fills sound like a
        // slowed-down reading of the translation instead of natural dubbing.
        ratio = Math.Clamp(ratio, 0.75, 1.6);

        var filters = new List<string>();
        if (Math.Abs(targetDuration - inputDuration) > 0.05)
        {
            var denominator = ratio >= 1.0 ? 2 : 4;
            filters.Add($"atempo={ratio.ToString(CultureInfo.InvariantCulture)}");
        }

        var filterArg = filters.Count > 0 ? $"-af {string.Join(",", filters)}" : "";
        await RunAsync(FfmpegPath,
            $"-y -i \"{audioPath}\" {filterArg} -ar 16000 -ac 1 \"{outputPath}\"",
            cancellationToken,
            capture: false);
        return outputPath;
    }

    private async Task<double> ProbeDurationAsync(string audioPath, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(audioPath, cancellationToken);
        return probe.DurationSeconds;
    }

    public async Task MixAndMuxAsync(
        string originalVideoPath,
        IReadOnlyList<SynthesizedClip> clips,
        string outputVideoPath,
        CancellationToken cancellationToken)
    {
        var audioTrack = await BuildAudioTrackAsync(clips, Path.GetDirectoryName(outputVideoPath)!, cancellationToken);
        await MuxAudioWithVideoAsync(originalVideoPath, audioTrack, outputVideoPath, cancellationToken);
    }

    public async Task<string> BuildAudioTrackAsync(IReadOnlyList<SynthesizedClip> clips, string workDirectory, CancellationToken cancellationToken)
    {
        var listPath = Path.Combine(workDirectory, "concat.txt");
        var builder = new StringBuilder();
        var ordered = clips.OrderBy(c => c.StartSeconds).ToList();
        var cursor = 0d;
        var piece = 0;
        foreach (var clip in ordered)
        {
            if (clip.StartSeconds > cursor + 0.05)
            {
                var silence = Path.Combine(workDirectory, $"gap-{piece}.wav");
                var gap = (clip.StartSeconds - cursor).ToString(CultureInfo.InvariantCulture);
                await RunAsync(FfmpegPath,
                    $"-y -f lavfi -i anullsrc=r=16000:cl=mono -t {gap} \"{silence}\"",
                    cancellationToken,
                    capture: false);
                builder.AppendLine($"file '{silence.Replace('\\', '/')}'");
            }

            builder.AppendLine($"file '{clip.AudioPath.Replace('\\', '/')}'");
            cursor = clip.StartSeconds + clip.DurationSeconds;
            piece++;
        }

        await File.WriteAllTextAsync(listPath, builder.ToString(), cancellationToken);
        var mixed = Path.Combine(workDirectory, "mixed.wav");
        await RunAsync(FfmpegPath,
            $"-y -f concat -safe 0 -i \"{listPath}\" -c copy \"{mixed}\"",
            cancellationToken,
            capture: false);
        return mixed;
    }

    public async Task MuxAudioWithVideoAsync(string videoPath, string audioPath, string outputVideoPath, CancellationToken cancellationToken)
    {
        await RunAsync(FfmpegPath,
            $"-y -i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 \"{outputVideoPath}\"",
            cancellationToken,
            capture: false);
    }

    /// <summary>Ducks the original audio to ~0.22 (≈ -13 dB) and mixes the voice track over it
    /// with output normalization disabled so the spoken track keeps its level (voice-over mode).</summary>
    public async Task MixVoiceOverAsync(string originalAudioPath, string voiceTrackPath, string outputAudioPath, CancellationToken cancellationToken)
    {
        await RunAsync(FfmpegPath,
            $"-y -i \"{originalAudioPath}\" -i \"{voiceTrackPath}\" -filter_complex \"[0:a]volume=0.22[bg];[bg][1:a]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[out]\" -map \"[out]\" -ar 16000 -ac 2 \"{outputAudioPath}\"",
            cancellationToken,
            capture: false);
    }

    private async Task<string> RunAsync(string fileName, string arguments, CancellationToken cancellationToken, bool capture = true, bool fromStdErr = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        _logger.LogDebug("Running {File} {Args}", fileName, arguments);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}. Ensure FFmpeg is installed and on PATH.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 && capture)
        {
            throw new InvalidOperationException($"{fileName} failed: {stderr}");
        }

        if (!capture && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {stderr}");
        }

        return fromStdErr ? stderr.ToString() : stdout.ToString();
    }

    private static double ParseAfter(string line, string token)
    {
        var idx = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return 0;
        }

        var rest = line[(idx + token.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
