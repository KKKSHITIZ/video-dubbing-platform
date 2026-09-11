using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VideoDubbing.Infrastructure.Localization;

/// <summary>
/// Production-ready orchestration client for enterprise Translation Cloud APIs
/// (HeyGen v3 <c>/v3/video-translations</c> / ElevenLabs <c>/v1/dubbing</c>).
/// Uploads an English video, polls the translation job until it settles, then streams
/// the French (lip-synced, original audio bed preserved) result to local disk.
/// </summary>
public sealed class VideoTranslationService
{
    private const int BufferBytes = 1 << 20; // 1 MiB
    private const string IngestRelativePath = "/v3/video-translations";
    private const string StatusRelativePathTemplate = "/v1/video_status?job_id={jobId}";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VideoTranslationService> _logger;

    public VideoTranslationService(
        IHttpClientFactory httpClientFactory,
        ILogger<VideoTranslationService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Translates <paramref name="localVideoFilePath"/> (English) into French and writes the
    /// finished localized video into <paramref name="outputDirectoryPath"/>. Returns the
    /// fully-qualified local path of the generated file.
    /// </summary>
    public async Task<string> ProcessVideoTranslationToFrenchAsync(
        string localVideoFilePath,
        string outputDirectoryPath,
        string apiBearerToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localVideoFilePath))
        {
            throw new ArgumentException("A source video path is required.", nameof(localVideoFilePath));
        }

        if (!File.Exists(localVideoFilePath))
        {
            throw new FileNotFoundException($"Source video not found: {localVideoFilePath}", localVideoFilePath);
        }

        if (string.IsNullOrWhiteSpace(outputDirectoryPath))
        {
            throw new ArgumentException("An output directory is required.", nameof(outputDirectoryPath));
        }

        if (string.IsNullOrWhiteSpace(apiBearerToken))
        {
            throw new ArgumentException("A bearer token is required.", nameof(apiBearerToken));
        }

        var http = _httpClientFactory.CreateClient("VideoTranslation");
        if (http.BaseAddress is null)
        {
            throw new InvalidOperationException("The 'VideoTranslation' HttpClient must have a BaseAddress. Configure VideoLocalization:BaseUrl (HeyGen / ElevenLabs / CAMB.AI API root).");
        }

        // a) Large-file multipart ingest with French target capabilities.
        TranslationJobResult job;
        try
        {
            job = await UploadVideoAsync(http, localVideoFilePath, apiBearerToken, ct);
            _logger.LogInformation("Translation job {JobId} created for {File}", job.EffectiveJobId, localVideoFilePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video ingest upload failed for {File}.", localVideoFilePath);
            throw new VideoTranslationException("The video ingest upload failed.", ex);
        }

        // b) Non-blocking resilient status polling.
        string downloadUrl;
        try
        {
            downloadUrl = await PollUntilFinishedAsync(http, job.EffectiveJobId, apiBearerToken, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translation job {JobId} failed during status polling.", job.EffectiveJobId);
            throw new VideoTranslationException("The translation job did not complete successfully.", ex);
        }

        // c) Memory-efficient binary stream ingestion to local disk.
        try
        {
            Directory.CreateDirectory(outputDirectoryPath);
            var target = await DownloadDubbedVideoAsync(http, downloadUrl, outputDirectoryPath, apiBearerToken, ct);
            _logger.LogInformation("French localization written to {Target}.", target);
            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Downloading the localized video failed.");
            throw new VideoTranslationException("Streaming the finished localized video to disk failed.", ex);
        }
    }

    private async Task<TranslationJobResult> UploadVideoAsync(
        HttpClient http,
        string videoPath,
        string token,
        CancellationToken ct)
    {
        var file = new FileInfo(videoPath);
        await using (var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true))
        using (var form = new MultipartFormDataContent())
        using (var fileContent = new StreamContent(stream, BufferBytes))
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GuessContentType(file.Extension));
            form.Add(fileContent, "video", file.Name);
            form.Add(new StringContent("en"), "input_language");
            form.Add(new StringContent("[\"fr\"]"), "target_languages");
            form.Add(new StringContent("true"), "voice_cloning");
            form.Add(new StringContent("true"), "lip_sync");

            using var request = new HttpRequestMessage(HttpMethod.Post, IngestRelativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = form;

            _logger.LogInformation("Uploading {FileName} ({Bytes} bytes) to {Endpoint}.", file.Name, file.Length, IngestRelativePath);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Ingest rejected with {Status}: {Body}", (int)response.StatusCode, Truncate(json));
                throw new HttpRequestException($"Ingest rejected with {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return ParseJobResult(json);
        }
    }

    private async Task<string> PollUntilFinishedAsync(
        HttpClient http,
        string jobId,
        string token,
        CancellationToken ct)
    {
        var statusPath = StatusRelativePathTemplate.Replace("{jobId}", Uri.EscapeDataString(jobId));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, statusPath);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                StatusEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<StatusEnvelope>(json, JsonOptions) ?? new StatusEnvelope();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Unparseable status payload for job {JobId}.", jobId);
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Status poll returned {Status} for job {JobId}.", (int)response.StatusCode, jobId);
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                var status = NormalizeStatus(envelope.EffectiveStatus);
                _logger.LogInformation("Job {JobId} status: {Status}", jobId, status);

                if (status == TranslationStatus.Succeeded)
                {
                    var url = envelope.ResolvedDownloadUrl;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        throw new InvalidOperationException($"Job {jobId} succeeded but no download URL was returned.");
                    }

                    return url;
                }

                if (status == TranslationStatus.Failed)
                {
                    _logger.LogError("Job {JobId} failed: {Detail}", jobId, envelope.EffectiveErrorMessage);
                    throw new InvalidOperationException($"Translation job {jobId} failed: {envelope.EffectiveErrorMessage}");
                }

                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Transient error while polling job {JobId}; retrying in {Seconds}s.", jobId, PollInterval.TotalSeconds);
                await Task.Delay(PollInterval, ct);
            }
        }
    }

    private async Task<string> DownloadDubbedVideoAsync(
        HttpClient http,
        string downloadUrl,
        string outputDirectory,
        string token,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var fileName = SafeFileName(downloadUrl) ?? $"french-dub-{DateTime.UtcNow:yyyyMMddHHmmss}.mp4";
        var targetPath = Path.Combine(outputDirectory, fileName);

        _logger.LogInformation("Streaming localized video to {Target}...", targetPath);
        await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, useAsync: true);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(fs, BufferBytes, ct);
        await fs.FlushAsync(ct);

        _logger.LogInformation("Localized video size: {Bytes} bytes.", new FileInfo(targetPath).Length);
        return targetPath;
    }

    private static TranslationJobResult ParseJobResult(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<TranslationJobResult>(json, JsonOptions);
            if (string.IsNullOrWhiteSpace(result?.EffectiveJobId))
            {
                throw new InvalidOperationException("Ingest response did not include a job id.");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Ingest response was not valid JSON.", ex);
        }
    }

    private static TranslationStatus NormalizeStatus(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "completed" or "completed_processing" or "succeeded" or "success" or "done"
            or "dubbing_succeeded" or "ended" => TranslationStatus.Succeeded,
            "failed" or "error" or "cancelled" or "canceled"
            or "dubbing_failed" or "card_failed" => TranslationStatus.Failed,
            "pending" or "queued" or "waiting" => TranslationStatus.Pending,
            "processing" or "running" or "in_progress" or "translating"
            or "dubbing" or "dubbing_processing" or "started" => TranslationStatus.Processing,
            _ => TranslationStatus.Unknown
        };
    }

    private static string Truncate(string value, int maxChars = 512)
        => value.Length <= maxChars ? value : value[..maxChars];

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

/// <summary>Normalized lifecycle state of an async translation job.</summary>
public enum TranslationStatus
{
    Pending = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
    Unknown = 4
}

/// <summary>Response payload returned by the multipart ingest endpoint.</summary>
public sealed record TranslationJobResult
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("job_id")]
    public string? JobId { get; init; }

    [JsonPropertyName("video_id")]
    public string? VideoId { get; init; }

    [JsonPropertyName("dubbing_id")]
    public string? DubbingId { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonIgnore]
    public string EffectiveJobId => Id ?? JobId ?? VideoId ?? DubbingId ?? RequestId ?? string.Empty;
}

/// <summary>Top-level status envelope tolerant of HeyGen <c>data</c>/<c>result</c> shapes.</summary>
public sealed record StatusEnvelope
{
    [JsonPropertyName("job_id")]
    public string? JobId { get; init; }

    [JsonPropertyName("dubbing_id")]
    public string? DubbingId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("card_status")]
    public string? CardStatus { get; init; }

    [JsonPropertyName("data")]
    public PayloadData? Data { get; init; }

    [JsonPropertyName("result")]
    public PayloadData? Result { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public string EffectiveStatus => CardStatus ?? Data?.Status ?? Result?.Status ?? Status ?? string.Empty;

    [JsonIgnore]
    public string EffectiveErrorMessage => Data?.ErrorMessage ?? Result?.ErrorMessage ?? Message ?? "no detail provided";

    [JsonIgnore]
    public string? ResolvedDownloadUrl => Data?.ResolvedDownloadUrl ?? Result?.ResolvedDownloadUrl;
}

/// <summary>Nested payload containing the vendor's progress detail and delivery URL.</summary>
public sealed record PayloadData
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("progress")]
    public int? Progress { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; init; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonIgnore]
    public string? ResolvedDownloadUrl => VideoUrl ?? DownloadUrl ?? Url;
}

/// <summary>Wrapper exception identifying the exact pipeline stage that failed.</summary>
public sealed class VideoTranslationException : Exception
{
    public VideoTranslationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VideoTranslationException(string message)
        : base(message)
    {
    }
}