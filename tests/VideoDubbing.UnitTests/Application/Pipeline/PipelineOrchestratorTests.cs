using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Pipeline;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.UnitTests.Application.Pipeline;

public sealed class PipelineOrchestratorTests
{
    private sealed class CountingStep : IPipelineStep
    {
        private readonly int _failuresBeforeSuccess;
        public int ExecuteCount { get; private set; }
        public CountingStep(int failuresBeforeSuccess = 0) => _failuresBeforeSuccess = failuresBeforeSuccess;
        public string Name => "Counting";
        public ProcessingStage Stage => ProcessingStage.Transcribing;
        public int ProgressPercent => 40;
        public Task ExecuteAsync(PipelineContext context, CancellationToken ct)
        {
            ExecuteCount++;
            if (ExecuteCount <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException("transient");
            }
            return Task.CompletedTask;
        }
    }

    private readonly Mock<IJobRepository> _jobs = new();
    private readonly Mock<IObjectStorage> _storage = new();
    private readonly Mock<IJobProgressPublisher> _progress = new();
    private readonly ProcessingOptions _options = new();

    private PipelineOrchestrator Create(IEnumerable<IPipelineStep> steps) => new(
        _jobs.Object,
        _storage.Object,
        steps,
        _progress.Object,
        Options.Create(_options),
        NullLogger<PipelineOrchestrator>.Instance);

    private static Job PendingJob() => new() { Status = JobStatus.Queued, DurationSeconds = 60 };

    [Fact]
    public async Task ProcessAsync_completes_and_marks_job_done()
    {
        var job = PendingJob();
        var step = new CountingStep();
        _jobs.Setup(x => x.GetWithDetailsAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _jobs.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var orchestrator = Create([step]);

        await orchestrator.ProcessAsync(job.Id, CancellationToken.None);

        job.Status.Should().Be(JobStatus.Completed);
        job.ProgressPercent.Should().Be(100);
        step.ExecuteCount.Should().Be(1);
        _progress.Verify(x => x.PublishAsync(job.Id, JobStatus.Completed, ProcessingStage.Completed, 100, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_retries_failed_step_up_to_configured_count()
    {
        var job = PendingJob();
        _options.RetryCount = 3;
        _jobs.Setup(x => x.GetWithDetailsAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _jobs.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var step = new CountingStep(failuresBeforeSuccess: 2);
        var orchestrator = Create([step]);

        await orchestrator.ProcessAsync(job.Id, CancellationToken.None);

        step.ExecuteCount.Should().Be(3); // fail, fail, succeed
        job.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task ProcessAsync_marks_failed_when_validation_errors_and_retries_exhausted()
    {
        var job = PendingJob();
        _options.RetryCount = 1;
        _jobs.Setup(x => x.GetWithDetailsAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _jobs.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var step = new CountingStep(failuresBeforeSuccess: 99);
        var orchestrator = Create([step]);

        var act = () => orchestrator.ProcessAsync(job.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("transient");
        job.Status.Should().Be(JobStatus.Failed);
        job.ErrorMessage.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_aborts_when_job_already_cancelled()
    {
        var job = new Job { Status = JobStatus.Cancelled };
        _jobs.Setup(x => x.GetWithDetailsAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var orchestrator = Create([new CountingStep()]);

        await orchestrator.ProcessAsync(job.Id, CancellationToken.None);

        job.Status.Should().Be(JobStatus.Cancelled);
        _progress.Verify(x => x.PublishAsync(It.IsAny<Guid>(), It.IsAny<JobStatus>(), It.IsAny<ProcessingStage>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
