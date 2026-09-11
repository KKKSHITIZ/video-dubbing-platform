using Microsoft.EntityFrameworkCore;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Infrastructure.Persistence;

public sealed class JobRepository : IJobRepository
{
    private readonly DubbingDbContext _db;

    public JobRepository(DubbingDbContext db) => _db = db;

    public async Task AddAsync(Job job, CancellationToken cancellationToken) =>
        await _db.Jobs.AddAsync(job, cancellationToken);

    public Task<Job?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    public Task<Job?> GetWithDetailsAsync(Guid id, CancellationToken cancellationToken) =>
        _db.Jobs
            .Include(j => j.Speakers)
            .Include(j => j.Segments).ThenInclude(s => s.Speaker)
            .Include(j => j.Artifacts)
            .Include(j => j.ProcessingLogs)
            .FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        _db.Jobs.CountAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Queued || j.Status == JobStatus.Processing, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}

public sealed class AuditService : IAuditService
{
    private readonly DubbingDbContext _db;

    public AuditService(DubbingDbContext db) => _db = db;

    public async Task RecordAsync(Guid? jobId, string action, string details, string? actor, CancellationToken cancellationToken)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            JobId = jobId,
            Action = action,
            Details = details,
            Actor = actor
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
