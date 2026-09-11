using VideoDubbing.Contracts.Jobs;

namespace VideoDubbing.Application.Jobs;

public sealed record UploadRequest(
    Stream Content,
    string FileName,
    string ContentType,
    long Length,
    IReadOnlyList<string> TargetLanguages,
    string SourceLanguage,
    string Mode = "Dub",
    string Subtitles = "Translated",
    string? Theme = null);

public interface IJobService
{
    Task<UploadJobResponse> UploadAsync(UploadRequest request, CancellationToken cancellationToken);
    Task<JobStatusResponse> GetStatusAsync(Guid jobId, CancellationToken cancellationToken);
    Task<TranscriptResponse> GetTranscriptAsync(Guid jobId, CancellationToken cancellationToken);

    Task<SpeakerTranscriptResponse> GetSpeakerTranscriptAsync(Guid jobId, CancellationToken cancellationToken);
    Task<(Stream Stream, string ContentType, string FileName)> DownloadArtifactAsync(Guid jobId, string kind, string language, CancellationToken cancellationToken);
    Task<(Stream Stream, string ContentType, string FileName)> DownloadTranscriptAsync(Guid jobId, string language, CancellationToken cancellationToken);
    Task<(Stream Stream, string ContentType, string FileName)> DownloadSourceAsync(Guid jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProcessingLogDto>> GetLogsAsync(Guid jobId, CancellationToken cancellationToken);
    Task RetryAsync(Guid jobId, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);
}
