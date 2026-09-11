using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Abstractions;

public interface IJobRepository
{
    Task AddAsync(Job job, CancellationToken cancellationToken);
    Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Job?> GetWithDetailsAsync(Guid id, CancellationToken cancellationToken);
    Task<int> CountActiveAsync(CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IAuditService
{
    Task RecordAsync(Guid? jobId, string action, string details, string? actor, CancellationToken cancellationToken);
}

public interface IJobProgressPublisher
{
    Task PublishAsync(Guid jobId, JobStatus status, ProcessingStage stage, int percent, string message, CancellationToken cancellationToken);
}

public interface IJobQueue
{
    Task EnqueueAsync(Guid jobId, string correlationId, CancellationToken cancellationToken);
}
