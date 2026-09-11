namespace VideoDubbing.Domain.Jobs;

public enum JobStatus
{
    Pending = 0,
    Queued = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5
}
