using Microsoft.AspNetCore.Mvc;
using VideoDubbing.Application.Jobs;
using VideoDubbing.Infrastructure.Localization;

namespace VideoDubbing.Api.Controllers;

[ApiController]
[Route("api/v1/jobs")]
public sealed class JobsController : ControllerBase
{
    private readonly IJobService _jobs;
    private readonly VideoTranslationService _translation;

    public JobsController(IJobService jobs, VideoTranslationService translation)
    {
        _jobs = jobs;
        _translation = translation;
    }

    /// <summary>Upload a source video and enqueue dubbing for one or more target languages.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(5_242_880_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Upload(
        IFormFile video,
        [FromForm] string? targetLanguages,
        [FromForm] string? sourceLanguage,
        [FromForm] string? mode,
        [FromForm] string? subtitles,
        [FromForm] string? remixTheme,
        CancellationToken cancellationToken)
    {
        if (video is null)
        {
            return BadRequest(new { error = "A video file is required." });
        }

        var languages = (targetLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await using var stream = video.OpenReadStream();
        var result = await _jobs.UploadAsync(new UploadRequest(
            stream,
            video.FileName,
            video.ContentType,
            video.Length,
            languages,
            sourceLanguage ?? "auto",
            mode ?? "Dub",
            subtitles ?? "Translated",
            remixTheme), cancellationToken);
        return AcceptedAtAction(nameof(GetStatus), new { jobId = result.JobId }, result);
    }

    [HttpGet("{jobId:guid}")]
    public async Task<IActionResult> GetStatus(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await _jobs.GetStatusAsync(jobId, cancellationToken));

    [HttpGet("{jobId:guid}/transcript")]
    public async Task<IActionResult> GetTranscript(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await _jobs.GetTranscriptAsync(jobId, cancellationToken));

    [HttpGet("{jobId:guid}/transcript/speakers")]
    public async Task<IActionResult> GetSpeakerTranscript(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await _jobs.GetSpeakerTranscriptAsync(jobId, cancellationToken));

    [HttpGet("{jobId:guid}/transcript/{language}")]
    public async Task<IActionResult> DownloadTranscript(Guid jobId, string language, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadTranscriptAsync(jobId, language, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/source")]
    public async Task<IActionResult> DownloadSource(Guid jobId, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadSourceAsync(jobId, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/video")]
    public async Task<IActionResult> DownloadVideo(Guid jobId, [FromQuery] string language, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadArtifactAsync(jobId, "video", language, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/subtitles")]
    public async Task<IActionResult> DownloadSubtitles(Guid jobId, [FromQuery] string language, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadArtifactAsync(jobId, "subtitles", language, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/vtt")]
    public async Task<IActionResult> DownloadVtt(Guid jobId, [FromQuery] string language, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadArtifactAsync(jobId, "vtt", language, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/audio")]
    public async Task<IActionResult> DownloadAudio(Guid jobId, CancellationToken cancellationToken)
    {
        var file = await _jobs.DownloadArtifactAsync(jobId, "audio", string.Empty, cancellationToken);
        return File(file.Stream, file.ContentType, file.FileName);
    }

    [HttpGet("{jobId:guid}/logs")]
    public async Task<IActionResult> GetLogs(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await _jobs.GetLogsAsync(jobId, cancellationToken));

    [HttpPost("{jobId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid jobId, CancellationToken cancellationToken)
    {
        await _jobs.RetryAsync(jobId, cancellationToken);
        return Accepted(new { jobId, status = "Queued" });
    }

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid jobId, CancellationToken cancellationToken)
    {
        await _jobs.CancelAsync(jobId, cancellationToken);
        return Accepted(new { jobId, status = "Cancelled" });
    }

    [HttpGet("{jobId:guid}/events")]
    public async Task StreamEvents(Guid jobId, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        var tcs = new TaskCompletionSource();
        await using var registration = cancellationToken.Register(() => tcs.TrySetResult());

        void Handler(Contracts.Progress.JobProgressEvent evt)
        {
            if (evt.JobId != jobId)
            {
                return;
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(evt);
            _ = Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            _ = Response.Body.FlushAsync(cancellationToken);
        }

        Infrastructure.Messaging.LoggingProgressPublisher.Progress += Handler;
        try
        {
            var status = await _jobs.GetStatusAsync(jobId, cancellationToken);
            await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(status)}\n\n", cancellationToken);
            await tcs.Task;
        }
        finally
        {
            Infrastructure.Messaging.LoggingProgressPublisher.Progress -= Handler;
        }
    }

    /// <summary>
    /// Convenience controller endpoint that drives the external Video Localization API
    /// (HeyGen v3 video-translations / ElevenLabs dubbing): uploads the selected video,
    /// waits for the (lip-synced) French result and streams it back to the caller.
    /// Requires an <c>Authorization: Bearer &lt;token&gt;</c> header.
    /// </summary>
    [HttpPost("localize")]
    [RequestSizeLimit(5_242_880_000)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Localize(
        IFormFile video,
        [FromHeader(Name = "Authorization")] string? authorization,
        CancellationToken cancellationToken)
    {
        if (video is null || video.Length == 0)
        {
            return BadRequest(new { error = "A source video file is required." });
        }

        if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "An 'Authorization: Bearer <token>' header is required." });
        }

        var apiBearerToken = authorization["Bearer ".Length..].Trim();
        var outputDir = Path.Combine(Path.GetTempPath(), "video-localization-output");
        var tempInput = Path.Combine(Path.GetTempPath(), $"localize-src-{Guid.NewGuid():N}{Path.GetExtension(video.FileName)}");
        await using (var input = new FileStream(tempInput, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await using var src = video.OpenReadStream();
            await src.CopyToAsync(input, cancellationToken);
        }

        try
        {
            var localizedPath = await _translation.ProcessVideoTranslationToFrenchAsync(tempInput, outputDir, apiBearerToken, cancellationToken);
            var fs = System.IO.File.OpenRead(localizedPath); // ownership transfers to FileStreamResult (disposed after streaming)
            var contentType = Path.GetExtension(localizedPath).ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".webm" => "video/webm",
                _ => "application/octet-stream"
            };
            return File(fs, contentType, Path.GetFileName(localizedPath));
        }
        finally
        {
            System.IO.File.Delete(tempInput);
        }
    }
}
