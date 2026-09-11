using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VideoDubbing.Application.Pipeline;
using VideoDubbing.Contracts.Jobs;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.IntegrationTests;

public sealed class JobsApiTests : IClassFixture<DubbingWebApplicationFactory>
{
    private readonly DubbingWebApplicationFactory _factory;

    public JobsApiTests(DubbingWebApplicationFactory factory) => _factory = factory;

    private static MultipartFormDataContent BuildUpload(string fileName = "sample.mp4", string languages = "es,fr")
    {
        var content = new MultipartFormDataContent();
        var bytes = new byte[1024];
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        content.Add(file, "video", fileName);
        content.Add(new StringContent(languages), "targetLanguages");
        content.Add(new StringContent("auto"), "sourceLanguage");
        return content;
    }

    [Fact]
    public async Task Upload_returns_accepted_with_job_id()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload();

        var response = await client.PostAsync("/api/v1/jobs/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<UploadJobResponse>();
        body!.JobId.Should().NotBeEmpty();
        body.TargetLanguages.Should().Equal(["es", "fr"]);
    }

    [Fact]
    public async Task Upload_rejects_unsupported_format()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload(fileName: "malware.exe");

        var response = await client.PostAsync("/api/v1/jobs/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_rejects_oversized_file()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload();
        var big = new byte[20 * 1024 * 1024];
        var file = new ByteArrayContent(big);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        content.Dispose();
        var content2 = new MultipartFormDataContent();
        content2.Add(file, "video", "sample.mp4");
        content2.Add(new StringContent("es"), "targetLanguages");

        var response = await client.PostAsync("/api/v1/jobs/upload", content2);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStatus_returns_job_details()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload();
        var created = await client.PostAsync("/api/v1/jobs/upload", content);
        var job = await created.Content.ReadFromJsonAsync<UploadJobResponse>();

        var response = await client.GetAsync($"/api/v1/jobs/{job!.JobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<JobStatusResponse>();
        status!.JobId.Should().Be(job.JobId);
        status.Status.Should().BeOneOf(JobStatus.Pending.ToString(), JobStatus.Queued.ToString());
        status.TargetLanguages.Should().Equal(["es", "fr"]);
    }

    [Fact]
    public async Task GetStatus_returns_404_for_missing_job()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Full_pipeline_produces_transcript_video_and_subtitles()
    {
        using var client = _factory.CreateClient();

        using var content = BuildUpload();
        var created = await client.PostAsync("/api/v1/jobs/upload", content);
        created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await created.Content.ReadFromJsonAsync<UploadJobResponse>();
        var jobId = job!.JobId;

        using (var scope = _factory.Services.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IPipelineOrchestrator>();
            await orchestrator.ProcessAsync(jobId, CancellationToken.None);
        }

        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/api/v1/jobs/{jobId}");
        status!.Status.Should().Be(JobStatus.Completed.ToString());
        status.ProgressPercent.Should().Be(100);

        var transcript = await client.GetFromJsonAsync<TranscriptResponse>($"/api/v1/jobs/{jobId}/transcript");
        transcript!.Segments.Should().NotBeEmpty();
        transcript.Speakers.Should().NotBeEmpty();
        transcript.Segments.First().Translations.Should().ContainKey("es");

        using var video = await client.GetAsync($"/api/v1/jobs/{jobId}/video?language=es");
        video.StatusCode.Should().Be(HttpStatusCode.OK);
        video.Content.Headers.ContentDisposition!.FileNameStar.Should().Contain("video");

        using var subtitles = await client.GetAsync($"/api/v1/jobs/{jobId}/subtitles?language=fr");
        subtitles.StatusCode.Should().Be(HttpStatusCode.OK);
        var srt = await subtitles.Content.ReadAsStringAsync();
        srt.Should().Contain("SPEAKER_");
        srt.Should().Contain("-->");

        var logs = await client.GetFromJsonAsync<List<ProcessingLogDto>>($"/api/v1/jobs/{jobId}/logs");
        logs!.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task Retry_failed_job_requeues()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload();
        var created = await client.PostAsync("/api/v1/jobs/upload", content);
        var job = await created.Content.ReadFromJsonAsync<UploadJobResponse>();
        var jobId = job!.JobId;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoDubbing.Infrastructure.Persistence.DubbingDbContext>();
        var entity = await db.Jobs.FindAsync(jobId);
        entity!.Status = JobStatus.Failed;
        entity.ErrorMessage = "forced failure";
        await db.SaveChangesAsync();

        var response = await client.PostAsync($"/api/v1/jobs/{jobId}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var readScope = _factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<VideoDubbing.Infrastructure.Persistence.DubbingDbContext>();
        var refreshed = await readDb.Jobs.FindAsync(jobId);
        refreshed!.Status.Should().Be(JobStatus.Queued);
        refreshed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_processing_job()
    {
        using var client = _factory.CreateClient();
        using var content = BuildUpload();
        var created = await client.PostAsync("/api/v1/jobs/upload", content);
        var job = await created.Content.ReadFromJsonAsync<UploadJobResponse>();

        var response = await client.PostAsync($"/api/v1/jobs/{job!.JobId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var status = await client.GetFromJsonAsync<JobStatusResponse>($"/api/v1/jobs/{job.JobId}");
        status!.Status.Should().Be(JobStatus.Cancelled.ToString());
    }
}
