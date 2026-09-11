namespace VideoDubbing.Application.Configuration;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    public int MaxUploadSizeMb { get; set; } = 512;
    public int MaxDurationMinutes { get; set; } = 10;
    public string[] AllowedFormats { get; set; } = ["mp4", "mov", "avi", "mkv"];
    public int MaxConcurrentUploads { get; set; } = 10;
    public int MaxConcurrentJobs { get; set; } = 4;
    public int ProcessingTimeoutMinutes { get; set; } = 30;
    public int RetryCount { get; set; } = 3;
    public int QueueLength { get; set; } = 100;
    public int RateLimitPermitLimit { get; set; } = 60;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public int StepDelayMs { get; set; }
}

public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public ProviderChain SpeechToText { get; set; } = new() { Primary = "Mock" };
    public ProviderChain Translation { get; set; } = new() { Primary = "Mock" };
    public ProviderChain Voice { get; set; } = new() { Primary = "ElevenLabs" };
    public ProviderChain Diarization { get; set; } = new() { Primary = "FfmpegSilence" };
    public string LipSync { get; set; } = "Passthrough";
    public string VoiceCliPath { get; set; } = string.Empty;
    public string LipSyncCliPath { get; set; } = string.Empty;
    public string CoquiCliPath { get; set; } = string.Empty;
    public string OpenVoiceCliPath { get; set; } = string.Empty;
    public string EdgeTtsCliPath { get; set; } = "edge-tts";
    public string[] ElevenLabsVoiceIds { get; set; } = [];
    public string VoskModelPath { get; set; } = string.Empty;
    public string WhisperModel { get; set; } = string.Empty;
    public string WhisperModelPath { get; set; } = string.Empty;
    public bool EnableFallback { get; set; } = true;
    public bool EnableCostAwareRouting { get; set; } = true;
    public int CostAwareShortJobSeconds { get; set; } = 120;
}

public sealed class ProviderChain
{
    public string Primary { get; set; } = "Mock";
    public string[] Fallbacks { get; set; } = [];
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "Local";
    public string LocalRoot { get; set; } = Path.Combine(Path.GetTempPath(), "video-dubbing");
    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class S3StorageOptions
{
    public string Bucket { get; set; } = "video-dubbing";
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public string AccessKey { get; set; } = "minio";
    public string SecretKey { get; set; } = "minio12345";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool RequireApiKey { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool EnableRateLimiting { get; set; } = true;
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
}

public sealed class AiSecretsOptions
{
    public const string SectionName = "AiSecrets";

    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string DeepgramApiKey { get; set; } = string.Empty;
    public string AssemblyAiApiKey { get; set; } = string.Empty;
    public string GoogleSpeechApiKey { get; set; } = string.Empty;
    public string GeminiApiKey { get; set; } = string.Empty;
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string DeepLApiKey { get; set; } = string.Empty;
    public string DeepLBaseUrl { get; set; } = "https://api-free.deepl.com";
    public string ElevenLabsApiKey { get; set; } = string.Empty;
    public string ElevenLabsBaseUrl { get; set; } = "https://api.elevenlabs.io";
    public string AzureSpeechKey { get; set; } = string.Empty;
    public string AzureSpeechRegion { get; set; } = "eastus";
    public string AzureSpeechVoice { get; set; } = "en-US-JennyNeural";
}

public sealed class ElevenLabsOptions
{
    public const string SectionName = "ElevenLabs";

    /// <summary>The xi-api-key used for the ElevenLabs Dubbing API. Falls back to AiSecrets:ElevenLabsApiKey.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.elevenlabs.io";

    /// <summary>Client-side wait between status polls while a dubbing job is processing.</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Maximum number of status polls before giving up.</summary>
    public int MaxPollAttempts { get; set; } = 120;
}

public sealed class VideoLocalizationOptions
{
    public const string SectionName = "VideoLocalization";

    /// <summary>Base URL of the Video Localization API (HeyGen / ElevenLabs / CAMB.AI style).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Bearer API key for the vendor.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Relative path of the multipart job-creation endpoint.</summary>
    public string UploadPath { get; set; } = "/v2/video/generate";

    /// <summary>
    /// Relative path of the status endpoint. Supports a <c>{job_id}</c> placeholder; when
    /// absent the job id is appended as a <c>job_id</c> query parameter.
    /// </summary>
    public string StatusPath { get; set; } = "/v1/video_status";

    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "fr";

    /// <summary>Delay between status polls.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Hard cap on status polls before giving up.</summary>
    public int MaxPollAttempts { get; set; } = 60;

    /// <summary>Per-request HTTP timeout (large uploads).</summary>
    public int HttpTimeoutSeconds { get; set; } = 180;

    /// <summary>Transient retry attempts per HTTP call (5xx / timeout / network blips).</summary>
    public int RetryCount { get; set; } = 3;
}
