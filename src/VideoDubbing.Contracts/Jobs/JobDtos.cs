namespace VideoDubbing.Contracts.Jobs;

public sealed record UploadJobResponse(
    Guid JobId,
    string Status,
    IReadOnlyList<string> TargetLanguages,
    DateTimeOffset CreatedAt,
    string Mode = "Dub",
    string Subtitles = "Translated");

public sealed record JobStatusResponse(
    Guid JobId,
    string Status,
    string Stage,
    int ProgressPercent,
    string SourceLanguage,
    string DetectedLanguage,
    IReadOnlyList<string> TargetLanguages,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Mode = "Dub",
    string Subtitles = "Translated");

public sealed record SpeakerDto(
    Guid Id,
    string Label,
    double SpeakingDurationSeconds,
    string VoiceId);

public sealed record TranscriptSegmentDto(
    int Sequence,
    Guid? SpeakerId,
    string SpeakerLabel,
    double StartSeconds,
    double EndSeconds,
    string SourceText,
    IReadOnlyDictionary<string, string> Translations);

public sealed record TranscriptResponse(
    Guid JobId,
    string DetectedLanguage,
    IReadOnlyList<SpeakerDto> Speakers,
    IReadOnlyList<TranscriptSegmentDto> Segments);

public sealed record TranscriptSpeakerSectionDto(
    Guid Id,
    string Label,
    double SpeakingDurationSeconds,
    IReadOnlyList<TranscriptSegmentDto> Segments);

/// <summary>A flat transcript chunk in the <c>[SpeakerId]: Text</c> response format.</summary>
public sealed record TranscriptChunkDto(
    Guid SpeakerId,
    string SpeakerLabel,
    string Speaker,
    double StartSeconds,
    double EndSeconds,
    string Text);

/// <summary>Transcript grouped speaker-by-speaker, with native display names for every target language,
/// plus a flat <see cref="Chunks"/> list in <c>[Id]: Text</c> form.</summary>
public sealed record SpeakerTranscriptResponse(
    Guid JobId,
    IReadOnlyDictionary<string, string> LanguageNames,
    IReadOnlyList<TranscriptSpeakerSectionDto> Speakers,
    IReadOnlyList<TranscriptChunkDto> Chunks);

public sealed record ArtifactDto(string Kind, string Language, string ContentType, long SizeBytes);

public sealed record ProcessingLogDto(DateTimeOffset Timestamp, string Level, string Stage, string Message);
