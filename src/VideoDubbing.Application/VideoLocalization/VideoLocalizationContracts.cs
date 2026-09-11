namespace VideoDubbing.Application.VideoLocalization;

/// <summary>Lifecycle state reported by the localization vendor's status endpoint.</summary>
public enum LocalizationJobStatus
{
    Queued = 0,
    Processing,
    Completed,
    Failed,
    Unknown
}

/// <summary>Parsed response from the job-creation (multipart upload) endpoint.</summary>
public sealed record JobCreationResult(
    string? JobId,
    string? RequestId,
    string? ErrorMessage,
    bool Succeeded);

/// <summary>Parsed response from the job status endpoint.</summary>
public sealed record ProcessingStatusResponse(
    string JobId,
    LocalizationJobStatus Status,
    string? DownloadUrl,
    string? ErrorMessage);

/// <summary>Result of streaming the generated file to local disk.</summary>
public sealed record LocalizationDownload(string LocalPath, long SizeBytes);

/// <summary>
/// Orchestrates a full video-translation job against an external Video Localization API
/// (HeyGen / ElevenLabs / CAMB.AI style): multipart upload &#8594; status polling &#8594;
/// streaming the finished file to the local output directory.
/// </summary>
public interface IVideoTranslationManager
{
    /// <summary>
    /// Uploads the source English video, polls until the vendor reports completion and
    /// streams the generated (lip-synced, localized) file into <paramref name="outputDirectory"/>.
    /// Returns the fully-qualified local path of the downloaded file.
    /// </summary>
    Task<string> InitiateVideoTranslationAsync(
        string localVideoPath,
        string outputDirectory,
        CancellationToken cancellationToken);
}