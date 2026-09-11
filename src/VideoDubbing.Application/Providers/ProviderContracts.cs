using VideoDubbing.Application.Media;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Providers;

public interface IDiarizationProvider
{
    string Name { get; }
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(string audioPath, CancellationToken cancellationToken);
}

public interface ISpeechToTextProvider
{
    string Name { get; }
    Task<(string DetectedLanguage, IReadOnlyList<TranscriptTurn> Turns)> TranscribeAsync(
        string audioPath,
        IReadOnlyList<SpeakerTurn> turns,
        string sourceLanguage,
        CancellationToken cancellationToken);
}

/// <summary>A transcript segment fed to a translation provider, carrying speaker + timing context
/// so the model can preserve conversation flow, tone and speaker mapping.</summary>
public sealed record TranslationSegment(
    string Text,
    string Speaker,
    double StartSeconds,
    double EndSeconds);

public sealed record TranslationRequest(
    IReadOnlyList<TranslationSegment> Segments,
    string SourceLanguage,
    string TargetLanguage,
    string? Theme = null);

public interface ITranslationProvider
{
    string Name { get; }

    /// <summary>Translates the whole ordered transcript, returning one translated string per
    /// input segment (same count and order). Duplicate utterances are handled per position.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public interface IVoiceProvider
{
    string Name { get; }
    Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null);
}

/// <summary>
/// Maintains a stable map between original Speaker IDs and per-project voice profile
/// hashes, so a given speaker keeps the same voice across every segment/language.
/// </summary>
public interface IVoiceProfileRegistry
{
    string ResolveVoiceId(Guid speakerId, string speakerLabel);
    VoiceProfile GetProfile(Guid speakerId);
}

public sealed record VoiceProfile(string VoiceId, string VoiceLabel);

/// <summary>
/// Best-effort lip synchronization: hands the time-aligned target audio and the
/// original video to an optional external engine (Wav2Lip / Video-Retalking) and
/// writes the resulting video. The default passthrough preserves the video as-is.
/// </summary>
public interface ILipSyncEngine
{
    string Name { get; }
    Task<string> LipSyncAsync(
        string originalVideoPath,
        string targetAudioPath,
        string outputVideoPath,
        string voiceLabel,
        CancellationToken cancellationToken);
}

public interface IProviderResolver
{
    IDiarizationProvider ResolveDiarization(double durationSeconds);
    ISpeechToTextProvider ResolveSpeechToText(double durationSeconds);
    ITranslationProvider ResolveTranslation(double durationSeconds);
    IVoiceProvider ResolveVoice(double durationSeconds);
    IReadOnlyList<IDiarizationProvider> DiarizationChain(double durationSeconds);
    IReadOnlyList<ISpeechToTextProvider> SpeechToTextChain(double durationSeconds);
    IReadOnlyList<ITranslationProvider> TranslationChain(double durationSeconds);
    IReadOnlyList<IVoiceProvider> VoiceChain(double durationSeconds);
    ILipSyncEngine ResolveLipSync();
}
