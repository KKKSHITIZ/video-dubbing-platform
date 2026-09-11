using Microsoft.Extensions.Logging;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Contracts.Progress;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Infrastructure.Messaging;

public sealed class LoggingProgressPublisher : IJobProgressPublisher
{
    private readonly ILogger<LoggingProgressPublisher> _logger;
    public static event Action<JobProgressEvent>? Progress;

    public LoggingProgressPublisher(ILogger<LoggingProgressPublisher> logger) => _logger = logger;

    public Task PublishAsync(Guid jobId, JobStatus status, ProcessingStage stage, int percent, string message, CancellationToken cancellationToken)
    {
        var evt = new JobProgressEvent(jobId, status.ToString(), stage.ToString(), percent, message, DateTimeOffset.UtcNow);
        _logger.LogInformation("Job {JobId} {Status} {Stage} {Percent}% {Message}", jobId, status, stage, percent, message);
        Progress?.Invoke(evt);
        return Task.CompletedTask;
    }
}
