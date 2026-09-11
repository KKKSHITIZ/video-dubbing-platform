using FluentAssertions;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.UnitTests.Domain;

public sealed class JobTests
{
    [Fact]
    public void New_job_is_pending_in_uploaded_stage()
    {
        var job = new Job();

        job.Status.Should().Be(JobStatus.Pending);
        job.Stage.Should().Be(ProcessingStage.Uploaded);
        job.ProgressPercent.Should().Be(0);
        job.CanRetry.Should().BeFalse();
        job.CanCancel.Should().BeTrue();
    }

    [Fact]
    public void MarkQueued_transitions_to_queued_and_updates_timestamp()
    {
        var job = new Job();
        job.MarkQueued();

        job.Status.Should().Be(JobStatus.Queued);
        job.CanCancel.Should().BeTrue();
        job.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MarkProcessing_sets_started_at_only_once()
    {
        var job = new Job();
        job.MarkProcessing();
        var first = job.StartedAt;
        job.MarkProcessing();

        job.Status.Should().Be(JobStatus.Processing);
        job.StartedAt.Should().Be(first);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150, 100)]
    [InlineData(-5, 0)]
    [InlineData(55, 55)]
    public void UpdateStage_clamps_progress_percent(int input, int expected)
    {
        var job = new Job();
        job.UpdateStage(ProcessingStage.Diarizing, input);

        job.Stage.Should().Be(ProcessingStage.Diarizing);
        job.ProgressPercent.Should().Be(expected);
    }

    [Fact]
    public void MarkCompleted_sets_full_progress_and_clears_error()
    {
        var job = new Job { ErrorMessage = "boom" };
        job.MarkFailed("boom");
        job.MarkCompleted();

        job.Status.Should().Be(JobStatus.Completed);
        job.Stage.Should().Be(ProcessingStage.Completed);
        job.ProgressPercent.Should().Be(100);
        job.ErrorMessage.Should().BeNull();
        job.CompletedAt.Should().NotBeNull();
        job.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void MarkFailed_allows_retry_and_truncates_long_errors()
    {
        var job = new Job();
        var longError = new string('x', 5000);
        job.MarkFailed(longError);

        job.Status.Should().Be(JobStatus.Failed);
        job.CanRetry.Should().BeTrue();
        job.ErrorMessage.Should().HaveLength(2000);
    }

    [Fact]
    public void MarkCancelled_transitions_and_stops_cancel()
    {
        var job = new Job();
        job.MarkCancelled();

        job.Status.Should().Be(JobStatus.Cancelled);
        job.CanCancel.Should().BeFalse();
        job.CanRetry.Should().BeTrue();
    }
}
