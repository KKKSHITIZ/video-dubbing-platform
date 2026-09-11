namespace VideoDubbing.Application.Media;

public sealed record MediaProbeResult(double DurationSeconds, string Format, int? Width, int? Height, bool HasAudio);

public sealed record SpeakerTurn(string SpeakerLabel, double StartSeconds, double EndSeconds);

public sealed record TranscriptTurn(
    string SpeakerLabel,
    double StartSeconds,
    double EndSeconds,
    string Text,
    string Language);

public sealed record SynthesizedClip(
    string SpeakerLabel,
    double StartSeconds,
    string AudioPath,
    double DurationSeconds,
    double TargetStartSeconds = 0d,
    double TargetEndSeconds = 0d,
    double AlignmentSpeed = 1.0);

public interface IMediaProcessor
{
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
    Task ExtractAudioAsync(string videoPath, string audioPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SpeakerTurn>> DetectSilenceTurnsAsync(string audioPath, CancellationToken cancellationToken);
    Task GenerateToneAsync(string outputPath, double durationSeconds, int frequencyHz, CancellationToken cancellationToken);
    Task MixAndMuxAsync(
        string originalVideoPath,
        IReadOnlyList<SynthesizedClip> clips,
        string outputVideoPath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a single concatenated mono 16kHz WAV track from the supplied clips with exact
    /// silence-filled gaps matching each clip's start offset.  Returns the path to the mixed
    /// audio file.
    /// </summary>
    Task<string> BuildAudioTrackAsync(IReadOnlyList<SynthesizedClip> clips, string workDirectory, CancellationToken cancellationToken);

    /// <summary>Muxes the original video stream with the supplied audio track and writes the result.</summary>
    Task MuxAudioWithVideoAsync(string videoPath, string audioPath, string outputVideoPath, CancellationToken cancellationToken);

    /// <summary>
    /// Voice-over mix: ducks the ORIGINAL audio (approx -13 dB) and overlays the synthesized voice
    /// track on top so the source stays audible underneath, like a documentary voice-over.
    /// Writes a 16kHz WAV ready for <see cref="MuxAudioWithVideoAsync"/>.
    /// </summary>
    Task MixVoiceOverAsync(string originalAudioPath, string voiceTrackPath, string outputAudioPath, CancellationToken cancellationToken);

    /// <summary>Crops [start,end] from the source audio as a 16kHz mono wav reference clip for voice cloning.</summary>
    Task ExtractAudioSegmentAsync(string audioPath, double startSeconds, double endSeconds, string outputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Dynamically time-stretches the audio to fit the exact [targetStartSeconds, targetEndSeconds]
    /// window, applying FFmpeg atempo (clamped 0.5..2.0). Returns the resulting path.
    /// </summary>
    Task<string> TimeStretchAsync(string audioPath, double targetEndSeconds, double targetStartSeconds, string outputPath, CancellationToken cancellationToken);
}
