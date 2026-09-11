using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.VideoLocalization;

namespace VideoDubbing.Infrastructure.Localization;

/// <summary>
/// Resilient orchestration of an external Video Localization API (HeyGen / ElevenLabs /
/// CAMB.AI style). Handles large multi-part uploads, asynchronous status polling and
/// streaming of the finished localized video directly to local disk.
/// </summary>
public sealed class VideoTranslationManager : IVideoTranslationManager
{
    private const int StreamBufferBytes = 1 << 20; // 1 MiB
    private readonly HttpClient _http;
    private readonly VideoLocalizationOptions _options;
    private readonly ILogger<VideoTranslationManager> _logger;

    public VideoTranslationManager(
        HttpClient http,
        IOptions<VideoLocalizationOptions> options,
        ILogger<VideoTranslationManager> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> InitiateVideoTranslationAsync(
        string localVideoPath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localVideoPath);
        if (!File.Exists(localVideoPath))
        {
            throw new FileNotFoundException($"Source video not found: {localVideoPath}", localVideoPath);
        }

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("VideoLocalization:BaseUrl is not configured. Set it to your HeyGen / ElevenLabs / CAMB.AI API root.");
        }

        Directory.CreateDirectory(outputDirectory);

        // a) Upload the source video and create the translation job.
        var created = await UploadVideoAsync(localVideoPath, cancellationToken);
        if (!created.Succeeded || string.IsNullOrWhiteSpace(created.JobId))
        {
            throw new InvalidOperationException($"Video localization job creation failed: {created.ErrorMessage}");
        }

        using var _ = _logger.BeginScope("{JobId}", created.JobId);
        _logger.LogInformation("Localization job {Job} created for {File}", created.JobId, localVideoPath);

        try
        {
            // b) Poll the status endpoint until the job reports a finished state.
            var status = await PollUntilFinishedAsync(created.JobId!, cancellationToken);
            if (string.IsNullOrWhiteSpace(status.DownloadUrl))
            {
                throw new InvalidOperationException($"Localization job {created.JobId} completed but returned no download URL.");
            }

            // c) Stream the generated file from the secure download URL to local disk.
            return await DownloadToFileAsync(status.DownloadUrl, outputDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Localization job {Job} cancelled by caller.", created.JobId);
            throw;
        }
    }

    private async Task<JobCreationResult> UploadVideoAsync(string localVideoPath, CancellationToken cancellationToken)
    {
        var url = Combine(_options.BaseUrl, _options.UploadPath);
        var file = new FileInfo(localVideoPath);
        await using var stream = new FileStream(localVideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, StreamBufferBytes, useAsync: true);
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream, StreamBufferBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(file.Extension));
        form.Add(fileContent, "video", file.Name);
        form.Add(new StringContent(_options.SourceLanguage), "source_language");
        form.Add(new StringContent(_options.TargetLanguage), "target_language");

        _logger.LogInformation("Uploading {File} ({Bytes} bytes) to {Url} as {Source} -> {Target}", file.Name, file.Length, url, _options.SourceLanguage, _options.TargetLanguage);
        var started = Stopwatch.GetTimestamp();
        using var response = await SendWithRetryAsync(
            () => _http.PostAsync(url, form, cancellationToken),
            "upload",
            cancellationToken);
        EnsureSuccess(response);

        using var doc = await ReadJsonAsync(response, cancellationToken);
        var root = doc.RootElement;
        var jobId = FindString(root, "job_id", "request_id", "video_id", "id");
        var requestId = FindString(root, "request_id");
        if (jobId is null && TryGet(root, "create_video_job_task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            jobId = FindString(task, "task_id", "id");
        }

        _logger.LogInformation("Upload completed in {ElapsedMs}ms; job id = {Job}", Stopwatch.GetElapsedTime(started).TotalMilliseconds, jobId);
        return new JobCreationResult(jobId, requestId, FindString(root, "message", "error"), Succeeded: jobId is not null);
    }

    private async Task<ProcessingStatusResponse> PollUntilFinishedAsync(string jobId, CancellationToken cancellationToken)
    {
        var url = _options.StatusPath.Contains("{job_id}", StringComparison.Ordinal)
            ? Combine(_options.BaseUrl, _options.StatusPath.Replace("{job_id}", Uri.EscapeDataString(jobId)))
            : $"{Combine(_options.BaseUrl, _options.StatusPath)}?job_id={Uri.EscapeDataString(jobId)}";

        for (var attempt = 1; attempt <= _options.MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await SendWithRetryAsync(() => _http.GetAsync(url, cancellationToken), "status", cancellationToken);
            EnsureSuccess(response);
            using var doc = await ReadJsonAsync(response, cancellationToken);
            var root = doc.RootElement;
            var status = ParseStatus(FindString(root, "status", "state") ?? string.Empty);
            var downloadUrl = ExtractDownloadUrl(root);
            var error = FindString(root, "message", "error", "error_message");

            _logger.LogDebug("Job {Job} status poll {Attempt}/{Max}: {Status}", jobId, attempt, _options.MaxPollAttempts, status);

            switch (status)
            {
                case LocalizationJobStatus.Completed:
                    _logger.LogInformation("Job {Job} completed after {Attempt} polls.", jobId, attempt);
                    return new ProcessingStatusResponse(jobId, status, downloadUrl, error);
                case LocalizationJobStatus.Failed:
                case LocalizationJobStatus.Unknown when !string.IsNullOrWhiteSpace(error) && error.Contains("fail", StringComparison.OrdinalIgnoreCase):
                    _logger.LogError("Job {Job} failed: {Error}", jobId, error);
                    throw new InvalidOperationException($"Localization job {jobId} failed: {error}");
                default:
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), cancellationToken);
                    break;
            }
        }

        throw new TimeoutException($"Localization job {jobId} did not finish within {_options.MaxPollAttempts} polls.");
    }

    private async Task<string> DownloadToFileAsync(string downloadUrl, string outputDirectory, CancellationToken cancellationToken)
    {
        var fileName = SafeFileName(downloadUrl) ?? $"localized-{_options.TargetLanguage}-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
        var targetPath = Path.Combine(outputDirectory, fileName);

        _logger.LogInformation("Streaming localized video from {Url} to {Target}", downloadUrl, targetPath);
        var started = Stopwatch.GetTimestamp();
        await using var source = await _http.GetStreamAsync(downloadUrl, cancellationToken);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferBytes, useAsync: true);
        var buffer = new byte[StreamBufferBytes];
        var total = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        await target.FlushAsync(cancellationToken);
        _logger.LogInformation("Downloaded {Bytes} bytes in {ElapsedMs}ms -> {Target}", total, Stopwatch.GetElapsedTime(started).TotalMilliseconds, targetPath);
        return targetPath;
    }

    /// <summary>
    /// Executes an HTTP call with exponential backoff for transient failures (network
    /// outages, server strain, 5xx / 429). The caller's CancellationToken is forwarded so
    /// heavy operations never hang a shutdown.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        string stage,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, _options.RetryCount); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await send();
                if (IsTransient(response.StatusCode))
                {
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Vendor returned {(HttpStatusCode)status} during {stage}.");
                }

                return response;
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "Transient failure during {Stage} (attempt {Attempt}/{Max}); retrying in {Delay}ms", stage, attempt, _options.RetryCount, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new HttpRequestException($"Failed to {stage} after {Math.Max(1, _options.RetryCount)} attempts.", last);
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException ||
        (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)code >= 500;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Vendor request failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Vendor returned an unparseable response.", ex);
        }
    }

    private static LocalizationJobStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "pending" or "queued" or "waiting" => LocalizationJobStatus.Queued,
        "processing" or "running" or "in_progress" or "translating" => LocalizationJobStatus.Processing,
        "completed" or "success" or "succeeded" or "done" => LocalizationJobStatus.Completed,
        "failed" or "error" or "cancelled" or "canceled" => LocalizationJobStatus.Failed,
        _ => LocalizationJobStatus.Unknown
    };

    private static string? ExtractDownloadUrl(JsonElement root)
    {
        if (TryGet(root, "result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            var url = FindString(result, "url", "video_url", "download_url");
            if (url is not null)
            {
                return url;
            }
        }

        if (TryGet(root, "data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var url = FindString(data, "url", "video", "video_url", "download_url");
            if (url is not null)
            {
                return url;
            }
        }

        return FindString(root, "url", "video_url", "download_url");
    }

    private static string? FindString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
        => element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static string Combine(string baseUrl, string relativePath)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return relativePath.StartsWith('/') ? $"{baseUrl}{relativePath}" : $"{baseUrl}/{relativePath}";
    }

    private static string GuessContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        _ => "application/octet-stream"
    };

    private static string? SafeFileName(string downloadUrl)
    {
        try
        {
            var name = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) || name == "/" ? null : name;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}