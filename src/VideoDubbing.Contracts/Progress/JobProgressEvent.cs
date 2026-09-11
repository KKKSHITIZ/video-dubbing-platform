namespace VideoDubbing.Contracts.Progress;

public sealed record JobProgressEvent(
    Guid JobId,
    string Status,
    string Stage,
    int ProgressPercent,
    string Message,
    DateTimeOffset Timestamp);
