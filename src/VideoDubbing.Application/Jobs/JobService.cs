using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Localization;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Storage;
using VideoDubbing.Contracts.Jobs;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Jobs;

public sealed class JobService : IJobService
{
    private static readonly HashSet<string> MagicAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "avi", "mkv"
    };

    private readonly IJobRepository _jobs;
    private readonly IObjectStorage _storage;
    private readonly IMediaProcessor _media;
    private readonly IJobQueue _queue;
    private readonly IAuditService _audit;
    private readonly ProcessingOptions _processing;
    private readonly ILogger<JobService> _logger;

    public JobService(
        IJobRepository jobs,
        IObjectStorage storage,
        IMediaProcessor media,
        IJobQueue queue,
        IAuditService audit,
        IOptions<ProcessingOptions> processing,
        ILogger<JobService> logger)
    {
        _jobs = jobs;
        _storage = storage;
        _media = media;
        _queue = queue;
        _audit = audit;
        _processing = processing.Value;
        _logger = logger;
    }

    public async Task<UploadJobResponse> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        ValidateUpload(request);

        if (!JobModes.TryParse(request.Mode, out var mode, out var modeError))
        {
            throw new InvalidOperationException(modeError);
        }

        if (!SubtitleStyles.TryParse(request.Subtitles, out var subtitleStyle))
        {
            throw new InvalidOperationException($"Unknown subtitle style '{request.Subtitles}'. Allowed: None, Translated, Original.");
        }

        var active = await _jobs.CountActiveAsync(cancellationToken);
        if (active >= _processing.QueueLength)
        {
            throw new InvalidOperationException("Processing queue is full. Retry later.");
        }

        var extension = NormalizeExtension(request.FileName);
        var job = new Job
        {
            OriginalFileName = Path.GetFileName(request.FileName),
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
            FileSizeBytes = request.Length,
            SourceLanguage = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : request.SourceLanguage,
            TargetLanguages = request.TargetLanguages.Select(l => l.Trim().ToLowerInvariant()).Distinct().ToList(),
            Mode = mode.ToString(),
            Subtitles = subtitleStyle.ToString(),
            Theme = string.IsNullOrWhiteSpace(request.Theme) ? null : request.Theme.Trim(),
            StorageKey = $"jobs/{Guid.NewGuid():N}/source.{extension}"
        };

        await using (request.Content)
        {
            await _storage.SaveAsync(request.Content, job.StorageKey, job.ContentType, cancellationToken);
        }

        var localPath = await CopyToTempAsync(job.StorageKey, cancellationToken);
        try
        {
            var probe = await _media.ProbeAsync(localPath, cancellationToken);
            if (probe.DurationSeconds > _processing.MaxDurationMinutes * 60)
            {
                await _storage.DeleteAsync(job.StorageKey, cancellationToken);
                throw new InvalidOperationException($"Video exceeds maximum duration of {_processing.MaxDurationMinutes} minutes.");
            }

            if (!probe.HasAudio)
            {
                await _storage.DeleteAsync(job.StorageKey, cancellationToken);
                throw new InvalidOperationException("Video does not contain an audio track.");
            }

            job.DurationSeconds = probe.DurationSeconds;
        }
        finally
        {
            TryDelete(localPath);
        }

        job.MarkQueued();
        await _jobs.AddAsync(job, cancellationToken);
        await _jobs.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(job.Id, "upload", $"Uploaded {job.OriginalFileName}", "api", cancellationToken);
        await _queue.EnqueueAsync(job.Id, job.CorrelationId, cancellationToken);

        _logger.LogInformation("Job {JobId} queued for {Languages}", job.Id, string.Join(',', job.TargetLanguages));

        return new UploadJobResponse(job.Id, job.Status.ToString(), job.TargetLanguages, job.CreatedAt, job.Mode, job.Subtitles);
    }

    public async Task<JobStatusResponse> GetStatusAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequireJob(jobId, cancellationToken);
        return MapStatus(job);
    }

    public async Task<TranscriptResponse> GetTranscriptAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetWithDetailsAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

        var speakers = job.Speakers
            .OrderBy(s => s.Label)
            .Select(s => new SpeakerDto(s.Id, s.Label, s.SpeakingDurationSeconds, s.VoiceId))
            .ToList();

        var segments = job.Segments
            .OrderBy(s => s.Sequence)
            .Select(s =>
            {
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(s.TranslationsJson)
                                   ?? new Dictionary<string, string>();
                return new TranscriptSegmentDto(
                    s.Sequence,
                    s.SpeakerId,
                    s.Speaker?.Label ?? "SPEAKER_00",
                    s.StartSeconds,
                    s.EndSeconds,
                    TranscriptNormalizer.CollapsePhraseRepeats(s.SourceText),
                    translations);
            })
            .ToList();

        return new TranscriptResponse(job.Id, job.DetectedLanguage, speakers, segments);
    }

    public async Task<SpeakerTranscriptResponse> GetSpeakerTranscriptAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetWithDetailsAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

        var languageNames = (job.TargetLanguages.Count > 0 ? job.TargetLanguages : new List<string> { job.DetectedLanguage })
            .Select(l => l.Trim().ToLowerInvariant())
            .Distinct()
            .ToDictionary(l => l, LanguageNames.DisplayName, StringComparer.OrdinalIgnoreCase);

        var segments = job.Segments
            .OrderBy(s => s.Sequence)
            .Select(s =>
            {
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(s.TranslationsJson)
                                   ?? new Dictionary<string, string>();
                return new TranscriptSegmentDto(
                    s.Sequence,
                    s.SpeakerId,
                    s.Speaker?.Label ?? "SPEAKER_00",
                    s.StartSeconds,
                    s.EndSeconds,
                    TranscriptNormalizer.CollapsePhraseRepeats(s.SourceText),
                    translations);
            })
            .ToList();

        var speakers = job.Speakers
            .OrderBy(s => s.Label)
            .Select(s =>
            {
                var speakerSegments = segments.Where(seg => seg.SpeakerId == s.Id).ToList();
                return new TranscriptSpeakerSectionDto(
                    s.Id,
                    s.Label,
                    s.SpeakingDurationSeconds,
                    speakerSegments);
            })
            .ToList();

        var chunks = segments
            .Select(seg => new TranscriptChunkDto(
                seg.SpeakerId ?? Guid.Empty,
                seg.SpeakerLabel,
                seg.SpeakerLabel,
                seg.StartSeconds,
                seg.EndSeconds,
                seg.SourceText))
            .ToList();

        return new SpeakerTranscriptResponse(job.Id, languageNames, speakers, chunks);
    }

    public async Task<(Stream Stream, string ContentType, string FileName)> DownloadTranscriptAsync(Guid jobId, string language, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetWithDetailsAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

        var lang = (language ?? job.DetectedLanguage).Trim().ToLowerInvariant();
        var isSource = lang == job.DetectedLanguage.Trim().ToLowerInvariant();
        if (!isSource &&
            job.TargetLanguages.All(l => !l.Equals(lang, StringComparison.OrdinalIgnoreCase)) &&
            !job.DetectedLanguage.Equals(lang, StringComparison.OrdinalIgnoreCase))
        {
            throw new KeyNotFoundException($"Transcript for language '{lang}' was not found.");
        }

        var lines = new List<string>();
        foreach (var s in job.Segments.OrderBy(s => s.Sequence))
        {
            var text = TranscriptNormalizer.CollapsePhraseRepeats(s.SourceText);
            if (!isSource)
            {
                var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(s.TranslationsJson)
                                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                text = translations.TryGetValue(lang, out var t) && !string.IsNullOrWhiteSpace(t)
                    ? t
                    : text;
            }
            lines.Add($"[{s.Speaker?.Label ?? "SPEAKER_00"}] {text}");
        }

        var content = string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return (stream, "text/plain; charset=utf-8", $"{jobId:N}-transcript-{lang}.txt");
    }

    public async Task<(Stream Stream, string ContentType, string FileName)> DownloadArtifactAsync(
        Guid jobId,
        string kind,
        string language,
        CancellationToken cancellationToken)
    {
        var job = await _jobs.GetWithDetailsAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

        var artifact = job.Artifacts.FirstOrDefault(a =>
                           a.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                           (string.IsNullOrWhiteSpace(language) || a.Language.Equals(language, StringComparison.OrdinalIgnoreCase)))
                       ?? throw new KeyNotFoundException($"Artifact '{kind}' for language '{language}' was not found.");

        var stream = await _storage.OpenReadAsync(artifact.StorageKey, cancellationToken);
        var fileName = $"{jobId:N}-{kind}-{artifact.Language}{GuessExtension(artifact.ContentType)}";
        return (stream, artifact.ContentType, fileName);
    }

    public async Task<(Stream Stream, string ContentType, string FileName)> DownloadSourceAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");
        var stream = await _storage.OpenReadAsync(job.StorageKey, cancellationToken);
        return (stream, job.ContentType, job.OriginalFileName);
    }

    public async Task<IReadOnlyList<ProcessingLogDto>> GetLogsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.GetWithDetailsAsync(jobId, cancellationToken)
                  ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

        return job.ProcessingLogs
            .OrderBy(l => l.CreatedAt)
            .Select(l => new ProcessingLogDto(l.CreatedAt, l.Level, l.Stage, l.Message))
            .ToList();
    }

    public async Task RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequireJob(jobId, cancellationToken);
        if (!job.CanRetry)
        {
            throw new InvalidOperationException($"Job {jobId} cannot be retried in status {job.Status}.");
        }

        job.Attempt++;
        job.ErrorMessage = null;
        job.CompletedAt = null;
        job.MarkQueued();
        job.UpdateStage(ProcessingStage.Uploaded, 0);
        await _jobs.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(job.Id, "retry", $"Retry attempt {job.Attempt}", "api", cancellationToken);
        await _queue.EnqueueAsync(job.Id, job.CorrelationId, cancellationToken);
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await RequireJob(jobId, cancellationToken);
        if (!job.CanCancel)
        {
            throw new InvalidOperationException($"Job {jobId} cannot be cancelled in status {job.Status}.");
        }

        job.MarkCancelled();
        await _jobs.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(job.Id, "cancel", "Cancellation requested", "api", cancellationToken);
    }

    private async Task<Job> RequireJob(Guid jobId, CancellationToken cancellationToken) =>
        await _jobs.GetAsync(jobId, cancellationToken)
        ?? throw new KeyNotFoundException($"Job {jobId} was not found.");

    private void ValidateUpload(UploadRequest request)
    {
        if (request.Length <= 0)
        {
            throw new InvalidOperationException("Empty uploads are not allowed.");
        }

        var maxBytes = _processing.MaxUploadSizeMb * 1024L * 1024L;
        if (request.Length > maxBytes)
        {
            throw new InvalidOperationException($"File exceeds maximum size of {_processing.MaxUploadSizeMb} MB.");
        }

        var extension = NormalizeExtension(request.FileName);
        if (!_processing.AllowedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase) || !MagicAllowed.Contains(extension))
        {
            throw new InvalidOperationException($"Unsupported format '{extension}'. Allowed: {string.Join(", ", _processing.AllowedFormats)}.");
        }

        if (request.TargetLanguages.Count == 0 && JobModes.TryParse(request.Mode, out var mode, out _) && !mode.IsCaptionOnly())
        {
            throw new InvalidOperationException("At least one target language is required.");
        }
    }

    private async Task<string> CopyToTempAsync(string key, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{Path.GetExtension(key)}");
        await using var source = await _storage.OpenReadAsync(key, cancellationToken);
        await using var dest = File.Create(temp);
        await source.CopyToAsync(dest, cancellationToken);
        return temp;
    }

    private static string NormalizeExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "qt" => "mov",
            _ => ext
        };
    }

    private static string GuessExtension(string contentType) => contentType switch
    {
        "video/mp4" => ".mp4",
        "application/x-subrip" => ".srt",
        "application/json" => ".json",
        "text/plain" => ".log",
        _ => ".bin"
    };

    private static JobStatusResponse MapStatus(Job job) => new(
        job.Id,
        job.Status.ToString(),
        job.Stage.ToString(),
        job.ProgressPercent,
        job.SourceLanguage,
        job.DetectedLanguage,
        job.TargetLanguages,
        job.ErrorMessage,
        job.CreatedAt,
        job.UpdatedAt,
        job.StartedAt,
        job.CompletedAt,
        job.Mode,
        job.Subtitles);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
