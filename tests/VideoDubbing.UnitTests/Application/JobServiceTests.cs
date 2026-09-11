using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Jobs;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.UnitTests.Application;

public sealed class JobServiceTests
{
    private readonly Mock<IJobRepository> _jobs = new();
    private readonly Mock<IObjectStorage> _storage = new();
    private readonly Mock<IMediaProcessor> _media = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly ProcessingOptions _options = new();

    private JobService CreateService()
    {
        _media.Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(60, "mp4", 1920, 1080, true));
        _storage.Setup(x => x.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Stream.Null);
        return new JobService(
            _jobs.Object,
            _storage.Object,
            _media.Object,
            _queue.Object,
            _audit.Object,
            Options.Create(_options),
            NullLogger<JobService>.Instance);
    }

    private static UploadRequest Request(
        string fileName = "sample.mp4",
        long length = 1024,
        IReadOnlyList<string>? languages = null,
        string contentType = "video/mp4") =>
        new(new MemoryStream([1, 2, 3]), fileName, contentType, length, languages ?? ["es"], "auto");

    [Fact]
    public async Task UploadAsync_rejects_empty_file()
    {
        var service = CreateService();
        var act = () => service.UploadAsync(Request(length: 0), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Empty uploads*");
    }

    [Fact]
    public async Task UploadAsync_rejects_oversized_file()
    {
        _options.MaxUploadSizeMb = 1;
        var service = CreateService();
        var act = () => service.UploadAsync(Request(length: 2 * 1024 * 1024), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum size*");
    }

    [Theory]
    [InlineData("sample.exe", false)]
    [InlineData("sample.txt", false)]
    [InlineData("sample.mp4", true)]
    [InlineData("sample.mov", true)]
    [InlineData("sample.qt", true)]
    [InlineData("sample.MKV", true)]
    public async Task UploadAsync_validates_format(string fileName, bool shouldPass)
    {
        var service = CreateService();
        var act = () => service.UploadAsync(Request(fileName: fileName), CancellationToken.None);

        if (shouldPass)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Unsupported format*");
        }
    }

    [Fact]
    public async Task UploadAsync_requires_at_least_one_target_language()
    {
        var service = CreateService();
        var act = () => service.UploadAsync(Request(languages: []), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*At least one target language*");
    }

    [Fact]
    public async Task UploadAsync_rejects_when_queue_is_full()
    {
        _options.QueueLength = 2;
        _jobs.Setup(x => x.CountActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var service = CreateService();
        var act = () => service.UploadAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*queue is full*");
    }

    [Fact]
    public async Task UploadAsync_rejects_video_longer_than_max_duration()
    {
        _options.MaxDurationMinutes = 1;
        var service = CreateService();
        _media.Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(120, "mp4", 1920, 1080, true));
        _storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var act = () => service.UploadAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*maximum duration*");
    }

    [Fact]
    public async Task UploadAsync_rejects_video_without_audio()
    {
        var service = CreateService();
        _media.Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(60, "mp4", 1920, 1080, false));
        _storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var act = () => service.UploadAsync(Request(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*audio track*");
    }

    [Fact]
    public async Task UploadAsync_enqueues_successful_job()
    {
        var service = CreateService();
        var result = await service.UploadAsync(Request(), CancellationToken.None);

        result.JobId.Should().NotBeEmpty();
        result.Status.Should().Be(JobStatus.Queued.ToString());
        result.TargetLanguages.Should().Equal(["es"]);
        _queue.Verify(x => x.EnqueueAsync(result.JobId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(x => x.RecordAsync(result.JobId, "upload", It.IsAny<string>(), "api", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetStatusAsync_maps_all_fields()
    {
        var job = new Job
        {
            Status = JobStatus.Processing,
            Stage = ProcessingStage.Translating,
            ProgressPercent = 55,
            SourceLanguage = "auto",
            DetectedLanguage = "en",
            TargetLanguages = ["es", "fr"],
            ErrorMessage = null
        };
        _jobs.Setup(x => x.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = CreateService();

        var status = await service.GetStatusAsync(job.Id, CancellationToken.None);

        status.Status.Should().Be("Processing");
        status.Stage.Should().Be("Translating");
        status.ProgressPercent.Should().Be(55);
        status.DetectedLanguage.Should().Be("en");
        status.TargetLanguages.Should().Equal(["es", "fr"]);
    }

    [Fact]
    public async Task GetStatusAsync_throws_for_unknown_job()
    {
        _jobs.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Job?)null);
        var service = CreateService();
        var act = () => service.GetStatusAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RetryAsync_rejects_job_not_in_retryable_state()
    {
        var job = new Job { Status = JobStatus.Completed };
        _jobs.Setup(x => x.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = CreateService();
        var act = () => service.RetryAsync(job.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RetryAsync_requeues_failed_job_and_increments_attempt()
    {
        var job = new Job { Status = JobStatus.Failed, Attempt = 1 };
        _jobs.Setup(x => x.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = CreateService();

        await service.RetryAsync(job.Id, CancellationToken.None);

        job.Attempt.Should().Be(2);
        job.Status.Should().Be(JobStatus.Queued);
        job.ErrorMessage.Should().BeNull();
        _queue.Verify(x => x.EnqueueAsync(job.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelAsync_rejects_completed_job()
    {
        var job = new Job { Status = JobStatus.Completed };
        _jobs.Setup(x => x.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = CreateService();
        var act = () => service.CancelAsync(job.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CancelAsync_cancels_processing_job()
    {
        var job = new Job { Status = JobStatus.Processing };
        _jobs.Setup(x => x.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var service = CreateService();

        await service.CancelAsync(job.Id, CancellationToken.None);

        job.Status.Should().Be(JobStatus.Cancelled);
        _audit.Verify(x => x.RecordAsync(job.Id, "cancel", It.IsAny<string>(), "api", It.IsAny<CancellationToken>()), Times.Once);
    }
}
