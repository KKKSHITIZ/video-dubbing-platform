using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IJobRepository _jobs;
    private readonly IObjectStorage _storage;
    private readonly IReadOnlyList<IPipelineStep> _steps;
    private readonly IJobProgressPublisher _progress;
    private readonly ProcessingOptions _processing;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IJobRepository jobs,
        IObjectStorage storage,
        IEnumerable<IPipelineStep> steps,
        IJobProgressPublisher progress,
        IOptions<ProcessingOptions> processing,
        ILogger<PipelineOrchestrator> logger)
    {
        _jobs = jobs;
        _storage = storage;
        _steps = steps.OrderBy(s => s.ProgressPercent).ToList();
        _progress = progress;
        _processing = processing.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(_processing.ProcessingTimeoutMinutes));

        var job = await _jobs.GetWithDetailsAsync(jobId, timeout.Token)
                  ?? throw new InvalidOperationException($"Job {jobId} was not found.");

        if (job.Status == JobStatus.Cancelled)
        {
            return;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "video-dubbing-work", jobId.ToString("N"));
        Directory.CreateDirectory(workDir);
        var context = new PipelineContext { Job = job, WorkDirectory = workDir };

        job.MarkProcessing();
        await PersistAsync(job, timeout.Token);

        try
        {
            foreach (var step in _steps)
            {
                await ThrowIfCancelled(jobId, timeout.Token);
                job.UpdateStage(step.Stage, step.ProgressPercent);
                AddLog(job, "Information", step.Name, $"Starting {step.Name}");
                await PersistAsync(job, timeout.Token);
                await _progress.PublishAsync(job.Id, job.Status, job.Stage, job.ProgressPercent, $"Starting {step.Name}", timeout.Token);

                if (_processing.StepDelayMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_processing.StepDelayMs), timeout.Token);
                }

                var attempt = 0;
                while (true)
                {
                    try
                    {
                        await step.ExecuteAsync(context, timeout.Token);
                        AddLog(job, "Information", step.Name, $"Completed {step.Name}");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt < _processing.RetryCount)
                    {
                        attempt++;
                        _logger.LogWarning(ex, "Step {Step} failed for job {JobId}, retry {Attempt}", step.Name, job.Id, attempt);
                        AddLog(job, "Warning", step.Name, $"Retry {attempt}: {ex.Message}");
                        await PersistAsync(job, timeout.Token);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), timeout.Token);
                    }
                }
            }

            job.MarkCompleted();
            AddLog(job, "Information", "Pipeline", "Job completed");
            await PersistAsync(job, timeout.Token);
            await _progress.PublishAsync(job.Id, job.Status, job.Stage, 100, "Completed", timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.MarkCancelled();
            AddLog(job, "Warning", "Pipeline", "Cancelled");
            await PersistAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.Id);
            job.MarkFailed(ex.Message);
            AddLog(job, "Error", job.Stage.ToString(), ex.Message);
            await PersistAsync(job, CancellationToken.None);
            await _progress.PublishAsync(job.Id, job.Status, job.Stage, job.ProgressPercent, ex.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean work directory {Dir}", workDir);
            }
        }
    }

    private async Task ThrowIfCancelled(Guid jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = await _jobs.GetAsync(jobId, cancellationToken);
        if (latest?.Status == JobStatus.Cancelled)
        {
            throw new OperationCanceledException("Job was cancelled.");
        }
    }

    private async Task PersistAsync(Job job, CancellationToken cancellationToken)
    {
        job.Touch();
        await _jobs.SaveChangesAsync(cancellationToken);
    }

    private static void AddLog(Job job, string level, string stage, string message)
    {
        job.ProcessingLogs.Add(new ProcessingLog
        {
            JobId = job.Id,
            Level = level,
            Stage = stage,
            Message = message
        });
    }
}
