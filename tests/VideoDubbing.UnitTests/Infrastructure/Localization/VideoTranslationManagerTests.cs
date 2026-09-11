using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Infrastructure.Localization;

namespace VideoDubbing.UnitTests.Infrastructure.Localization;

public sealed class VideoTranslationManagerTests
{
    private static VideoLocalizationOptions DefaultOptions() => new()
    {
        BaseUrl = "https://vendor.example",
        PollIntervalSeconds = 0,
        MaxPollAttempts = 5,
        HttpTimeoutSeconds = 30,
        RetryCount = 2
    };

    private static VideoTranslationManager CreateManager(
        Queue<HttpResponseMessage> responses,
        VideoLocalizationOptions? options = null)
    {
        var handler = new StubHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://vendor.example") };
        return new VideoTranslationManager(
            http,
            Options.Create(options ?? DefaultOptions()),
            NullLogger<VideoTranslationManager>.Instance);
    }

    [Fact]
    public async Task Initiate_uploads_then_polls_then_streams_result()
    {
        var sampleVideo = WriteTempVideo([0x00, 0x01, 0x02, 0x03, 0x04]);
        var outputDir = CreateTempOutputDir();
        try
        {
            var responses = new Queue<HttpResponseMessage>();
            // 1) create job
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"request_id":"req-1","job_id":"job-1"}"""));
            // 2) poll processing
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-1","status":"processing"}"""));
            // 3) poll completed with download url
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-1","status":"completed","result":{"url":"https://vendor.example/dl/localized.mp4"}}"""));
            // 4) download
            responses.Enqueue(ContentResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("localized-mp4-bytes"), "video/mp4"));

            var manager = CreateManager(responses);
            var path = await manager.InitiateVideoTranslationAsync(sampleVideo, outputDir, CancellationToken.None);

            path.Should().Be(Path.Combine(outputDir, "localized.mp4"));
            File.ReadAllBytes(path).Should().Equal(Encoding.UTF8.GetBytes("localized-mp4-bytes"));
        }
        finally
        {
            File.Delete(sampleVideo);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Initiate_throws_when_status_reports_failed()
    {
        var sampleVideo = WriteTempVideo([0x00, 0x01, 0x02]);
        var outputDir = CreateTempOutputDir();
        try
        {
            var responses = new Queue<HttpResponseMessage>();
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-9","request_id":"r9"}"""));
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-9","status":"failed","error_message":"license required"}"""));

            var manager = CreateManager(responses);
            var act = async () => await manager.InitiateVideoTranslationAsync(sampleVideo, outputDir, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Localization job job-9 failed: license required");
        }
        finally
        {
            File.Delete(sampleVideo);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Initiate_exhausts_attempts_then_times_out()
    {
        var sampleVideo = WriteTempVideo([0x00]);
        var outputDir = CreateTempOutputDir();
        var slowOptions = DefaultOptions();
        slowOptions.MaxPollAttempts = 3;
        try
        {
            var responses = new Queue<HttpResponseMessage>();
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-7"}"""));
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-7","status":"processing"}"""));
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-7","status":"processing"}"""));
            responses.Enqueue(JsonResponse(HttpStatusCode.OK, """{"job_id":"job-7","status":"processing"}"""));

            var manager = CreateManager(responses, slowOptions);
            var act = async () => await manager.InitiateVideoTranslationAsync(sampleVideo, outputDir, CancellationToken.None);

            await act.Should().ThrowAsync<TimeoutException>()
                .WithMessage("*did not finish within 3 polls*");
        }
        finally
        {
            File.Delete(sampleVideo);
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_source_file_throws_before_hitting_network()
    {
        var manager = CreateManager(new Queue<HttpResponseMessage>());
        var act = async () => await manager.InitiateVideoTranslationAsync(
            Path.Combine(Path.GetTempPath(), "does-not-exist.mp4"),
            Path.GetTempPath(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ContentResponse(HttpStatusCode status, byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static string WriteTempVideo(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string CreateTempOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vl-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHandler(Queue<HttpResponseMessage> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responses.Dequeue());
    }
}