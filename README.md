# AI-Powered Video Dubbing Platform (.NET)

A production-oriented, configuration-driven platform that translates and dubs videos
containing multiple speakers from a source language into one or more target languages.
It preserves speaker identity, conversation flow, timing/synchronization, natural voice
characteristics, and subtitle alignment.

Built with **.NET 8**, **ASP.NET Core**, **EF Core**, **MassTransit + RabbitMQ**, a clean
onion/clean-architecture layout, an extensible **AI provider abstraction layer**, and full
automated test coverage. This repository is intentionally **Docker-free** (per requirement
waiver) — everything can be run natively in Visual Studio Code / VS or as .NET processes.

---

## Table of Contents

1. [Features](#features)
2. [Solution Layout](#solution-layout)
3. [Running the Platform](#running-the-platform)
4. [Quickstart (No external services)](#quickstart-no-external-services)
5. [Configuration](#configuration)
6. [AI Providers](#ai-providers)
7. [API Overview](#api-overview)
8. [Testing](#testing)
9. [Observability](#observability)
10. [Documentation](#documentation)
11. [Walkthrough Video](#walkthrough-video)

---

## Features

- **Video upload** via REST (`multipart/form-data`) with format/size/duration validation.
- **Speaker diarization** (multiple speakers, boundaries, durations, consistent labels).
- **Speech recognition** with automatic language detection and timestamped transcripts.
- **Translation** into multiple target languages preserving tone and speaker mapping.
- **AI voice generation** with per-speaker voice consistency.
- **Audio-video synchronization** and subtitle (SRT) generation.
- **Output artifacts**: dubbed video, JSON transcript, SRT subtitles, processing logs.
- **Job lifecycle APIs**: upload, status, transcripts, download, retry, cancel, logs.
- **Configurable architecture** — everything (limits, providers, storage, queue, DB,
  security, secrets) is driven by configuration, not code.
- **Customizable AI providers** with a provider abstraction layer + **fallback** and
  **cost-aware routing**.
- **Scalable pipeline** via a job queue (MassTransit/RabbitMQ) and horizontal workers.
- **Fault tolerance** (retries, timeouts, graceful failure, cancellation).
- **Observability** (structured logging via Serilog, Prometheus metrics, health checks).
- **Security** (input/file validation, path-traversal protection, rate limiting,
  optional API-key auth).

---

## Solution Layout

```
.
├── VideoDubbingPlatform.sln
├── Directory.Build.props
├── README.md                      ← you are here
├── ARCHITECTURE.md                ← high-level architecture & scaling
├── DESIGN_DECISIONS.md            ← trade-offs and rationale
├── docs/
│   ├── API.md                     ← REST API reference
│   ├── CONFIGURATION.md           ← configuration & environment-variable guide
│   └── postman/                   ← sample Postman collection
├── config/
│   ├── appsettings.sample.json    ← full annotated sample configuration
│   └── .env.sample                ← environment-variable equivalents
├── src/
│   ├── VideoDubbing.Domain          (entities, enums, state machine — no dependencies)
│   ├── VideoDubbing.Contracts       (shared DTOs & messaging contracts)
│   ├── VideoDubbing.Application     (use cases, job service, pipeline, provider interfaces)
│   ├── VideoDubbing.Infrastructure  (EF Core, storage, queue, provider implementations)
│   ├── VideoDubbing.Api             (REST API host, middleware, rate limiting, metrics)
│   └── VideoDubbing.Worker          (background worker consuming the job queue)
└── tests/
    ├── VideoDubbing.UnitTests       (65 tests)
    └── VideoDubbing.IntegrationTests (8 API + end-to-end pipeline tests)
```

The solution follows **Clean Architecture / Onion** principles:

- **Domain** has zero dependencies and models the core state machine (`Job`).
- **Application** defines ports (interfaces) for persistence, storage, media, queue, and AI
  providers, plus the processing pipeline and orchestration.
- **Infrastructure** implements those ports (EF Core, S3/Local storage, MassTransit,
  real/Mock AI providers).
- **Api / Worker** are the composition roots that wire everything from configuration.

### Project references

```
Domain ← Contracts
Application → Domain, Contracts
Infrastructure → Domain, Application, Contracts
Api → Application, Infrastructure, Contracts
Worker → Application, Infrastructure, Contracts
Tests → Api/Application/Infrastructure/Contracts/Domain
```

---

## Running the Platform

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [FFmpeg](https://ffmpeg.org) with `ffprobe` on `PATH` for real media processing
  (Windows: `winget install Gyan.FFmpeg`)
- (Optional) PostgreSQL and/or RabbitMQ for production-like mode

### Quickstart (No external services) — one command

Fastest path — uses SQLite, an in-process job queue, and Mock AI providers, so a **single
API process** runs the whole pipeline (upload → diarize → transcribe → translate → voice →
mux → export) end to end. Zero infrastructure required.

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run-dev.ps1
```

That script sets the demo overrides automatically (it locates FFmpeg if installed via
`winget install Gyan.FFmpeg`), starts the API, and opens your browser. The dashboard at
**http://localhost:5043** has the full walkthrough UI: upload a video (or click **Upload demo
video**), watch the live pipeline with the functional-requirements tracker, and use the
dubbed video players, extracted-audio player, transcripts, SRT subtitles and processing logs.
Swagger UI with all endpoints is available at **http://localhost:5043/swagger**.

Equivalent overrides if you want to run manually:

```bash
Database__Provider=Sqlite
Messaging__Provider=None
Messaging__NoOpProcessInProcess=true   # process jobs in-process (demo mode)
Storage__Provider=Local
Storage__LocalRoot=./data
Providers__SpeechToText__Primary=Vosk
Providers__Translation__Primary=DeepL
Providers__Voice__Primary=GoogleTts
Providers__Diarization__Primary=FfmpegSilence
```

> **Note on voice/dubbing in this mode:** with `Vosk` (offline transcription), `DeepL→MyMemory`
> (real translation, key-free fallback) and `GoogleTts` (key-free native-language TTS) the demo
> dubbs the **actual speech** of the original audio into French and other languages without any
> AI API keys. `Mock`/`LocalFfmpeg` remain available as last-resort fallbacks.

### Production-like mode (PostgreSQL + RabbitMQ)

1. Start PostgreSQL and RabbitMQ (e.g. local installs or any container you manage).
2. Configure connection strings:

```bash
ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=videodubbing;Username=postgres;Password=postgres"
ConnectionStrings__RabbitMq="amqp://guest:guest@localhost:5672"
Database__Provider=Postgres
Messaging__Provider=RabbitMq
```

3. Run the API and one or more Workers.

The API creates the schema on startup (`EnsureCreated`). The Worker consumes
`ProcessJobMessage` from the queue and runs the dubbing pipeline.

### Optional: SQLite for local single-node runs

```bash
Database__Provider=Sqlite
ConnectionStrings__Sqlite="Data Source=./videodubbing.db"
```

---

## Configuration

> Copy `config/appsettings.sample.json` into `src/VideoDubbing.Api/appsettings.json` and fill
> in the values below: keys, URLs, storage folders, and queue modes.

### External Video Localization API (HeyGen / ElevenLabs / CAMB.AI style)

`IVideoTranslationManager` (`VideoTranslationManager`) lets the platform hand a video to a
third-party localization API that re-voices (English → French etc.) while preserving music,
SFX, speaker voice and lip-sync, then downloads the finished file back to disk.

| Key | Default | Purpose |
|-----|---------|---------|
| `VideoLocalization:BaseUrl` | *(required)* | API root, e.g. `https://api.heygen.com` |
| `VideoLocalization:ApiKey`   | `` | Bearer token sent automatically |
| `VideoLocalization:UploadPath` | `/v2/video/generate` | Multipart create-job endpoint (fields: `video`, `source_language`, `target_language`) |
| `VideoLocalization:StatusPath` | `/v1/video_status` | Poll endpoint; use `{job_id}` placeholder or `?job_id=` |
| `VideoLocalization:SourceLanguage` / `TargetLanguage` | `en` / `fr` | Language pair |
| `VideoLocalization:PollIntervalSeconds` | `5` | Status poll cadence |
| `VideoLocalization:MaxPollAttempts` | `60` | Hard cap before timeout |
| `VideoLocalization:HttpTimeoutSeconds` | `180` | Per-request timeout (large uploads) |
| `VideoLocalization:RetryCount` | `3` | Exponential-backoff retries on 5xx / timeouts |

HTTP client is a resilient typed `HttpClient` (base address, bearer auth, JSON accept). It
polls until `completed`, streams the result from the `result.url` field, and exposes a
convenience endpoint `POST /api/v1/jobs/localize` (multipart upload → localized MP4 back).

All settings are configuration-driven via `appsettings.json`, environment variables, user
secrets, or any .NET configuration source. **Nothing is hardcoded.**

Full annotated sample: [`config/appsettings.sample.json`](config/appsettings.sample.json)
and env-var equivalents in [`config/.env.sample`](config/.env.sample).

See [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) for a complete reference.

Key sections:

| Section | Purpose |
|---------|---------|
| `Processing` | Upload size / duration / formats, concurrency, timeout, retries, queue length |
| `Providers`  | AI provider selection, fallback chains, cost-aware routing |
| `Storage`    | Local vs S3/MinIO, root path, S3 credentials |
| `Database`   | Provider (Postgres/Sqlite/InMemory), connection strings |
| `Messaging`  | Provider (RabbitMq/None) |
| `Security`   | API key enforcement, rate limiting |
| `Media`      | FFmpeg / FFprobe paths |
| `AiSecrets`  | API keys for OpenAI, Deepgram, Gemini, DeepL, ElevenLabs, etc. |

Secrets (API keys) should be provided via **environment variables, user-secrets, or a
secret manager** — never committed to source.

---

## AI Providers

The platform abstracts AI capabilities behind three provider interfaces plus diarization:

```csharp
IDiarizationProvider      // speaker diarization
ISpeechToTextProvider     // transcription
ITranslationProvider      // translation
IVoiceProvider            // voice synthesis
```

A central `IProviderResolver` reads configured chains and resolves providers **at runtime**,
so switching providers is a **configuration change, never a code change**.

- **Diarization:** `FfmpegSilence` (local), `Pyannote` (stub)
- **Speech-to-Text:** `Vosk` (**key-free offline transcription of the original audio**, small English model auto-downloaded once into `data/models/`), `Mock`, `OpenAiWhisper`, `Deepgram`, `AssemblyAI` (stub), `GoogleSpeechToText` (stub)
- **Dubbing (whole video):** `api/dubbing` controller — one-shot ElevenLabs Dubbing (real AI, exact source pacing; needs `ElevenLabs:ApiKey`)
- **Translation:** `Mock`, `OpenAi`, `Gemini` (stub), `Claude` (stub), `DeepL` (**real client**, free endpoint by default), `MyMemory` (**key-free real translation**, public API)
- **Voice:** `ElevenLabs` (**real client**, `eleven_multilingual_v2`), `GoogleTts` (**key-free native-language TTS**, e.g. genuine French audio with no account), `Sapi` (**offline Windows TTS**), `LocalFfmpeg` (local tones), `XTTS-Bark` (open-source CLI bridge), `AzureSpeech` (stub), `Coqui` (stub), `OpenVoice` (stub)

**Provider chains** support ordered fallback: if the primary fails, the chain tries the
fallbacks in order (see `PipeLine` step retry + provider chain logic).

> Note: `Properties/launchSettings.json` does **not** pin provider primaries anymore —
> `appsettings.json` (or env vars / launch settings you add yourself) is the single source
> of truth for the chains.

**Real French dubbing (DeepL + ElevenLabs):** set `AiSecrets:DeepLApiKey` and
`AiSecrets:ElevenLabsApiKey`, then point the chains at the real providers:

```jsonc
"Providers": {
  "EnableCostAwareRouting": false,            // critical: short jobs must not drop to LocalFfmpeg
  "Translation": { "Primary": "DeepL", "Fallbacks": [ "MyMemory", "OpenAi", "Mock" ] },
  "Voice":        { "Primary": "ElevenLabs", "Fallbacks": [ "LocalFfmpeg" ] }
}
```

- DeepL free tier uses `https://api-free.deepl.com` (switch `AiSecrets:DeepLBaseUrl` to
  `https://api.deepl.com` on a Pro plan). Segments are batched in a single call, order-preserved.
- ElevenLabs synthesizes 16 kHz mono WAV per segment via `eleven_multilingual_v2` with
  `output_format=pcm_16000`, so the pipeline's atempo alignment and track mixing work as-is.
  Each speaker maps to a stable voice (default: Rachel, Domi, Bella, Antoni, Adam);

### One-shot ElevenLabs Dubbing (whole video)

`POST /api/dubbing/translate` runs the whole dubbing on ElevenLabs' Dubbing engine
(transcription → translation → synthesis → mixing server-side), so the resulting audio track
keeps the **original duration and pacing** exactly. Configure the key at `ElevenLabs:ApiKey`
(or legacy `AiSecrets:ElevenLabsApiKey`):

```bash
# create -- returns { "dubbingId": "..." }
curl -X POST http://localhost:5043/api/dubbing/translate \
  -F "file=@movie.mp4" -F "targetLanguage=fr" -F "sourceLanguage=en"

# poll -- status: dubbing / dubbed / failed / dubbing_popped
curl http://localhost:5043/api/dubbing/<dubbingId>

# download dubbed audio (matches source duration) + translated transcript
curl -o dubbed-fr.mp3 http://localhost:5043/api/dubbing/<dubbingId>/audio/fr
curl http://localhost:5043/api/dubbing/<dubbingId>/transcript/fr
curl -X DELETE http://localhost:5043/api/dubbing/<dubbingId>   # release ElevenLabs storage
```

**Upload limits:** `Processing.MaxUploadSizeMb` (default **5000 MB** = 5 GB) drives the Kestrel
and multipart body limits; `Processing.MaxDurationMinutes` (default **10**) rejects videos longer
than that; `Processing.RateLimitPermitLimit` / `RateLimitWindowSeconds` control the global rate
limiter (default 100 requests / 600 s).
  override with `Providers:ElevenLabsVoiceIds`. 429/5xx calls back off automatically.
- **No keys yet?** Translation falls back to `MyMemory` (real translations, free public API),
  and `GoogleTts` speaks every segment in the native target language (real French audio, no
  account); if that endpoint is unreachable, `Sapi` (Windows `System.Speech`) speaks real
  words in the best installed voice.
  You never hear test tones. Voice chain: `ElevenLabs` → `GoogleTts` → `Sapi` → `LocalFfmpeg`.

**Voice consistency & cloning:** each original speaker is assigned a **stable voice-profile
hash** (`VoiceProfileRegistry`, stored as `Speaker.VoiceId`) so Speaker 1 keeps the same
voice across every segment and target language. When `XTTS-Bark` is configured, a short
reference clip is cut from the source audio (`ref-<speaker>.wav`) and passed to the engine
for **voice cloning**.

**Timing alignment (AV sync):** every generated clip is time-stretched with FFmpeg `atempo`
(clamped 0.5–2.0) to fit the exact original segment timestamps, then concat-mixed and muxed
with the source video. The aligned clips + speeds are exported in the structured
`transcript.json` artifact (`AlignedAudioClips`).

**Best-effort lip-sync:** set `Providers:LipSyncCliPath` to a Wav2Lip/Video-Retalking CLI to
hand the time-aligned audio + original video to the engine; `Passthrough` (default) simply
muxes the aligned audio over the source video.

**Subtitles:** per-language **SRT** (`/subtitles`) and **VTT** (`/vtt`) are generated from the
aligned timestamps.

**Cost-aware routing:** for short jobs (≤ `CostAwareShortJobSeconds`), a cheaper `cheap`
provider can be injected ahead of the paid one, e.g. using a local model for short clips.

> Cloud providers that require API keys throw a clear, actionable error when their key is
> absent, so the system degrades gracefully and can fall back to another provider.

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the model-selection strategy.

---

## API Overview

| Method | Path | Description |
|--------|------|-------------|
| `POST`   | `/api/v1/jobs/upload`               | Upload a video and enqueue dubbing |
| `GET`    | `/api/v1/jobs/{jobId}`               | Get processing status |
| `GET`    | `/api/v1/jobs/{jobId}/transcript`    | Get transcript + translations |
| `GET`    | `/api/v1/jobs/{jobId}/video?language=` | Download dubbed video |
| `GET`    | `/api/v1/jobs/{jobId}/subtitles?language=` | Download SRT subtitles |
| `GET`    | `/api/v1/jobs/{jobId}/vtt?language=` | Download VTT subtitles |
| `GET`    | `/api/v1/jobs/{jobId}/logs`          | Get processing logs |
| `POST`   | `/api/v1/jobs/{jobId}/retry`         | Retry a failed/cancelled job |
| `POST`   | `/api/v1/jobs/{jobId}/cancel`        | Cancel a pending/processing job |
| `GET`    | `/api/v1/jobs/{jobId}/events`        | (SSE) live progress stream |
| `POST`   | `/api/v1/jobs/localize`              | Drive external Video Localization API (upload → translate → download) |
| `GET`    | `/health`                            | Health check |
| `GET`    | `/metrics`                           | Prometheus metrics |

Interactive Swagger UI is available at `/swagger` when running the API.

Full reference, including request/response examples: [`docs/API.md`](docs/API.md).

---

## Testing

```bash
dotnet test VideoDubbingPlatform.sln
```

- **Unit tests (111)** — domain state machine, upload validation, provider routing/fallback,
  storage security, subtitle formatting, pipeline orchestration & steps, provider behavior,
  transcript normalization, Video Localization manager (upload → poll → stream download).
- **Integration tests (8)** — boot the real API host (WebApplicationFactory) against a
  SQLite database with a stubbed media processor and drive upload → status → transcript →
  download, retry, cancel, and a full end-to-end pipeline run.

CI runs `dotnet build` + `dotnet test` (see `.github/workflows/ci.yml`).

---

## Observability

- **Structured logging** — Serilog (console by default; pluggable sinks).
- **Metrics** — OpenTelemetry with Prometheus exporter (`/metrics`).
- **Health checks** — `/health`.
- **Progress streaming** — Server-Sent Events (`/jobs/{id}/events`).
- Each job carries a `CorrelationId` for request tracing.

---

## Documentation

| Document | Description |
|----------|-------------|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | High-level architecture, component interactions, pipeline, queue, scaling, failure recovery, deployment, trade-offs, diagrams |
| [`docs/ARCHITECTURE_REFERENCE.md`](docs/ARCHITECTURE_REFERENCE.md) | **Fact-based reference** mirroring the actual code: overall architecture, code organization, configuration, AI abstraction, workflow, queue, scalability, fault tolerance, API, Docker status, challenges & decisions |
| [`DESIGN_DECISIONS.md`](DESIGN_DECISIONS.md) | Key design decisions and rationale |
| [`docs/API.md`](docs/API.md) | REST API reference |
| [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) | Configuration & env-var guide |
| [`config/appsettings.sample.json`](config/appsettings.sample.json) | Sample configuration |
| [`config/.env.sample`](config/.env.sample) | Sample env-var overrides |

---

## Usage

Upload a video and dub it into one or more target languages:

```powershell
curl.exe -X POST "http://localhost:5043/api/v1/jobs/upload" `
  -F "video=@movie.mp4" -F "targetLanguage=fr" -F "targetLanguage=es"
```

Poll status, view transcripts, and download the dubbed video via the [Jobs API](#api-overview).
Swagger UI: **http://localhost:5043/swagger**.
