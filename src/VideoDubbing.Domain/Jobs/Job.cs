namespace VideoDubbing.Domain.Jobs;

public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public ProcessingStage Stage { get; set; } = ProcessingStage.Uploaded;
    public int ProgressPercent { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public double? DurationSeconds { get; set; }
    public string SourceLanguage { get; set; } = "auto";
    public string DetectedLanguage { get; set; } = string.Empty;
    public List<string> TargetLanguages { get; set; } = new();
    public string Mode { get; set; } = JobMode.Dub.ToString();
    public string Subtitles { get; set; } = SubtitleStyle.Translated.ToString();
    public string? Theme { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int Attempt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    public ICollection<Speaker> Speakers { get; set; } = new List<Speaker>();
    public ICollection<TranscriptSegment> Segments { get; set; } = new List<TranscriptSegment>();
    public ICollection<Artifact> Artifacts { get; set; } = new List<Artifact>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
    public ICollection<ProcessingLog> ProcessingLogs { get; set; } = new List<ProcessingLog>();

    private JobMode? _modeKind;
    private SubtitleStyle? _subtitleStyle;

    public JobMode ModeKind => _modeKind ??= JobModes.TryParse(Mode, out var mode, out _) ? mode : JobMode.Dub;
    public SubtitleStyle SubtitleStyleKind => _subtitleStyle ??= SubtitleStyles.TryParse(Subtitles, out var style) ? style : SubtitleStyle.Translated;

    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Cancelled;
    public bool CanCancel => Status is JobStatus.Pending or JobStatus.Queued or JobStatus.Processing;

    public void MarkQueued()
    {
        Status = JobStatus.Queued;
        Touch();
    }

    public void MarkProcessing()
    {
        Status = JobStatus.Processing;
        StartedAt ??= DateTimeOffset.UtcNow;
        Touch();
    }

    public void UpdateStage(ProcessingStage stage, int percent)
    {
        Stage = stage;
        ProgressPercent = Math.Clamp(percent, 0, 100);
        Touch();
    }

    public void MarkCompleted()
    {
        Status = JobStatus.Completed;
        Stage = ProcessingStage.Completed;
        ProgressPercent = 100;
        CompletedAt = DateTimeOffset.UtcNow;
        ErrorMessage = null;
        Touch();
    }

    public void MarkFailed(string error)
    {
        Status = JobStatus.Failed;
        ErrorMessage = error.Length > 2000 ? error[..2000] : error;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkCancelled()
    {
        Status = JobStatus.Cancelled;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
