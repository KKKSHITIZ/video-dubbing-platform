namespace VideoDubbing.Domain.Jobs;

public sealed class ProcessingLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Level { get; set; } = "Information";
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Job? Job { get; set; }
}
