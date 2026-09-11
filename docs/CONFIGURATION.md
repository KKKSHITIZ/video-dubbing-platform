# Configuration Guide

Every behavioral knob of the platform is configuration-driven. There are **no hardcoded
settings**. Configuration is read by the standard ASP.NET configuration system, so values can
come from:

- `appsettings.json` / `appsettings.{Environment}.json`
- Environment variables (`Section__Key`) — the recommended way to override
- User secrets (`dotnet user-secrets`) for local dev
- Any other .NET configuration provider

**Convention:** nested keys are written flat in environment variables using `__` as the
separator, e.g. `Processing__MaxUploadSizeMb`, `AiSecrets__OpenAiApiKey`.

Reference sample: [`config/appsettings.sample.json`](../config/appsettings.sample.json)
Environment variable equivalents: [`config/.env.sample`](../config/.env.sample)

---

## Top-level sections

| Section | Purpose |
|---------|---------|
| `ConnectionStrings` | Postgres & RabbitMQ connection strings (also DB via `Database:Provider`) |
| `Processing` | Upload/queue/concurrency/timeout/rate-limit limits |
| `Providers` | AI provider selection, fallback chains, cost-aware routing |
| `Storage` | Object storage provider and credentials |
| `Database` | Persistence provider selection |
| `Messaging` | Job queue transport selection |
| `Security` | API key auth and rate limiting |
| `Media` | FFmpeg / FFprobe binary paths |
| `AiSecrets` | API keys for AI providers (use env vars / secret manager) |
| `Serilog` | Logging sinks and levels |

---

## ConnectionStrings

| Key | Default | Description |
|-----|---------|-------------|
| `ConnectionStrings__Postgres` | `Host=localhost;…` | Postgres connection string (used when `Database:Provider=Postgres`) |
| `ConnectionStrings__RabbitMq` | `amqp://guest:guest@localhost:5672` | RabbitMQ connection string (used when `Messaging:Provider=RabbitMq`) |
| `ConnectionStrings__Sqlite` | `Data Source=videodubbing.db` | SQLite connection string (used when `Database:Provider=Sqlite`) |

---

## Processing

| Key | Default | Description |
|-----|---------|-------------|
| `Processing__MaxUploadSizeMb` | `512` | Max upload size in MB |
| `Processing__MaxDurationMinutes` | `10` | Max accepted video duration (minutes) |
| `Processing__AllowedFormats` | `["mp4","mov","avi","mkv"]` | Allowed file extensions |
| `Processing__MaxConcurrentUploads` | `10` | Concurrent upload slot limit |
| `Processing__MaxConcurrentJobs` | `4` | Max jobs a single worker processes concurrently |
| `Processing__ProcessingTimeoutMinutes` | `30` | Per-job timeout before considered failed |
| `Processing__RetryCount` | `3` | Retry attempts for transient job failures |
| `Processing__QueueLength` | `100` | Max queued jobs before uploads are rejected |
| `Processing__RateLimitPermitLimit` | `60` | Upload request permit limit per window |
| `Processing__RateLimitWindowSeconds` | `60` | Rate-limit window length |

---

## Providers

AI provider selection. Chains support a `Primary` plus ordered `Fallbacks`.

| Key | Default | Description |
|-----|---------|-------------|
| `Providers__EnableFallback` | `true` | Allow fallback to next provider on failure |
| `Providers__EnableCostAwareRouting` | `true` | Use cheap provider for short jobs |
| `Providers__CostAwareShortJobSeconds` | `120` | Job duration threshold (s) for cost-aware routing |
| `Providers__SpeechToText__Primary` | `Mock` | STT provider |
| `Providers__SpeechToText__Fallbacks` | `[OpenAiWhisper,Deepgram]` | STT fallbacks |
| `Providers__Translation__Primary` | `Mock` | Translation provider |
| `Providers__Translation__Fallbacks` | `[MyMemory,OpenAi,DeepL]` | Translation fallbacks |
| `Providers__Voice__Primary` | `ElevenLabs` | Voice synthesis provider |
| `Providers__Voice__Fallbacks` | `[GoogleTts,Sapi,LocalFfmpeg]` | Voice fallbacks |
| `Providers__Diarization__Primary` | `FfmpegSilence` | Diarization provider |
| `Providers__Diarization__Fallbacks` | `[]` | Diarization fallbacks |

**Available providers**

- Diarization: `FfmpegSilence`, `Pyannote`
- Speech-to-Text: `Mock`, `OpenAiWhisper`, `Deepgram`, `AssemblyAI`, `GoogleSpeechToText`
- Translation: `Mock`, `OpenAi`, `Gemini`, `Claude`, `DeepL`, `MyMemory` (key-free, public API)
- Voice: `ElevenLabs`, `GoogleTts` (key-free native-language TTS), `Sapi` (offline Windows TTS), `LocalFfmpeg`, `AzureSpeech`, `Coqui`, `OpenVoice`

> Real providers require matching keys in `AiSecrets`. Missing keys produce a clear error and
> allow the chain to fall back. `GoogleTts` needs no key (public endpoint, short segments, low
> rate); `Sapi` needs no network and works entirely offline via Windows voices.

---

## Storage

| Key | Default | Description |
|-----|---------|-------------|
| `Storage__Provider` | `Local` | `Local` or `S3` |
| `Storage__LocalRoot` | `%TEMP%/video-dubbing` | Local storage root directory |
| `Storage__S3__Bucket` | `video-dubbing` | S3/MinIO bucket |
| `Storage__S3__ServiceUrl` | `http://localhost:9000` | MinIO/S3 endpoint |
| `Storage__S3__AccessKey` | `minio` | Access key |
| `Storage__S3__SecretKey` | `minio12345` | Secret key |
| `Storage__S3__Region` | `us-east-1` | Region |
| `Storage__S3__ForcePathStyle` | `true` | Path-style addressing (MinIO) |

---

## Database

| Key | Default | Description |
|-----|---------|-------------|
| `Database__Provider` | `Postgres` | `Postgres`, `Sqlite` or `InMemory` |

- `Postgres` → uses `ConnectionStrings__Postgres`.
- `Sqlite` → uses `ConnectionStrings__Sqlite` (e.g. `Data Source=./videodubbing.db`).
- `InMemory` → in-memory provider (tests / scratch runs; not persisted).

The schema is created automatically on startup (`EnsureCreated`).

---

## Messaging

| Key | Default | Description |
|-----|---------|-------------|
| `Messaging__Provider` | `RabbitMq` | `RabbitMq` or `None` |

- `RabbitMq` → MassTransit over `ConnectionStrings__RabbitMq`.
- `None` → in-process queue (good for local single-node dev, zero infra).

---

## Security

| Key | Default | Description |
|-----|---------|-------------|
| `Security__RequireApiKey` | `false` | Require `X-Api-Key` header |
| `Security__ApiKey` | `""` | Accepted API key value |
| `Security__EnableRateLimiting` | `true` | Enable fixed-window upload rate limiting |

---

## Media

| Key | Default | Description |
|-----|---------|-------------|
| `Media__FfmpegPath` | `ffmpeg` | FFmpeg binary (name or absolute path) |
| `Media__FfprobePath` | `ffprobe` | FFprobe binary (name or absolute path) |

Ensure FFmpeg/FFprobe are on `PATH` (or set absolute paths) for real media processing.

---

## AiSecrets

| Key | Used by |
|-----|---------|
| `AiSecrets__OpenAiApiKey` | OpenAiWhisper, OpenAi (translation) |
| `AiSecrets__OpenAiBaseUrl` | Override OpenAI-compatible endpoint |
| `AiSecrets__DeepgramApiKey` | Deepgram |
| `AiSecrets__AssemblyAiApiKey` | AssemblyAI |
| `AiSecrets__GoogleSpeechApiKey` | GoogleSpeechToText |
| `AiSecrets__GeminiApiKey` | Gemini |
| `AiSecrets__AnthropicApiKey` | Claude |
| `AiSecrets__DeepLApiKey` | DeepL |
| `AiSecrets__DeepLBaseUrl` | DeepL endpoint (`https://api-free.deepl.com` free / `https://api.deepl.com` Pro) |
| `AiSecrets__ElevenLabsApiKey` | ElevenLabs |
| `AiSecrets__ElevenLabsBaseUrl` | ElevenLabs endpoint (`https://api.elevenlabs.io`) |
| `AiSecrets__AzureSpeechKey` / `AiSecrets__AzureSpeechRegion` | AzureSpeech |

> **Never commit real keys.** Use user-secrets locally and a secret manager / environment
> variables in CI and production.

---

## VideoLocalization

| Key | Used by |
|-----|---------|
| `VideoLocalization:BaseUrl` | `IVideoTranslationManager`/`VideoTranslationManager` API root (required) |
| `VideoLocalization:ApiKey` | Bearer token sent on every vendor request |
| `VideoLocalization:UploadPath` | Multipart create-job endpoint (default `/v2/video/generate`) |
| `VideoLocalization:StatusPath` | Status poll endpoint (default `/v1/video_status`; `{job_id}` placeholder or `?job_id=`) |
| `VideoLocalization:SourceLanguage` / `TargetLanguage` | Language pair (default `en` → `fr`) |
| `VideoLocalization:PollIntervalSeconds` | Poll cadence (default `5`) |
| `VideoLocalization:MaxPollAttempts` | Hard cap on polls before timeout (default `60`) |
| `VideoLocalization:HttpTimeoutSeconds` | Per-request HTTP timeout, large uploads (default `180`) |
| `VideoLocalization:RetryCount` | Exponential-backoff retries on 5xx/timeout (default `3`) |
