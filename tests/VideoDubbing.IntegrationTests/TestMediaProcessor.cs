using System.Text;
using VideoDubbing.Application.Media;

namespace VideoDubbing.IntegrationTests;

/// <summary>
/// A media processor stub that avoids real FFmpeg binaries so the full
/// processing pipeline can run in integration tests. It synthesises a small
/// valid-ish file per operation.
/// </summary>
public sealed class TestMediaProcessor : IMediaProcessor
{
    private readonly Queue<MediaProbeResult> _probeResults;

    public TestMediaProcessor(params MediaProbeResult[] probeResults)
    {
        _probeResults = new Queue<MediaProbeResult>(probeResults);
    }

    public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = _probeResults.Count > 0 ? _probeResults.Dequeue() : new MediaProbeResult(60, "mp4", 1920, 1080, true);
        return Task.FromResult(result);
    }

    public Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        File.WriteAllText(audioPath, "audio-data");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SpeakerTurn>> DetectSilenceTurnsAsync(string audioPath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SpeakerTurn>>(
        [
            new SpeakerTurn("SPEAKER_00", 0, 2),
            new SpeakerTurn("SPEAKER_01", 2, 4),
            new SpeakerTurn("SPEAKER_00", 4, 6)
        ]);

    public Task GenerateToneAsync(string outputPath, double durationSeconds, int frequencyHz, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, $"tone:{frequencyHz}:{durationSeconds}");
        return Task.CompletedTask;
    }

    public Task MixAndMuxAsync(
        string originalVideoPath,
        IReadOnlyList<SynthesizedClip> clips,
        string outputVideoPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputVideoPath)!);
        var content = string.Join('|', clips.Select(c => $"{c.SpeakerLabel}:{c.StartSeconds}"));
        File.WriteAllText(outputVideoPath, content, Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task<string> BuildAudioTrackAsync(IReadOnlyList<SynthesizedClip> clips, string workDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var mixed = Path.Combine(workDirectory, "mixed.wav");
        File.WriteAllText(mixed, string.Join('|', clips.Select(c => $"{c.SpeakerLabel}:{c.StartSeconds}")), Encoding.UTF8);
        return Task.FromResult(mixed);
    }

    public Task MixVoiceOverAsync(string originalAudioPath, string voiceTrackPath, string outputAudioPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputAudioPath)!);
        File.WriteAllText(outputAudioPath, "vo-mix", Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task MuxAudioWithVideoAsync(string videoPath, string audioPath, string outputVideoPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputVideoPath)!);
        File.WriteAllText(outputVideoPath, "muxed", Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task ExtractAudioSegmentAsync(string audioPath, double startSeconds, double endSeconds, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, $"ref:{startSeconds}:{endSeconds}");
        return Task.CompletedTask;
    }

    public Task<string> TimeStretchAsync(string audioPath, double targetEndSeconds, double targetStartSeconds, string outputPath, CancellationToken cancellationToken)
    {
        File.Copy(audioPath, outputPath, overwrite: true);
        return Task.FromResult(outputPath);
    }
}
