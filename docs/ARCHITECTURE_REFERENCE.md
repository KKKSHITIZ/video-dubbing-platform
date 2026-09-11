# Architecture Reference

A practical, fact-based reference for the Video Dubbing Platform. It reflects the **actual current code**, not intended design. Companion docs: `ARCHITECTURE.md` (conceptual), `DESIGN_DECISIONS.md`, `docs/API.md`, `docs/CONFIGURATION.md`.

---

## 1. Overall Architecture

Clean-architecture solution: `VideoDubbingPlatform.sln`, all projects `net8.0`. Dependencies point inward; the Domain knows nothing about the outside world.

```
┌─────────────────────────────────────────────────────────────────┐
│  VideoDubbing.Api        HTTP API (controllers, DI, appsettings) │
│  VideoDubbing.Worker     Hosted worker (queue consumer, CLI)     │
├─────────────────────────────────────────────────────────────────┤
│  VideoDubbing.Application  Use cases, pipeline, orchestrator,    │
│                             providers contracts, job service     │
├─────────────────────────────────────────────────────────────────┤
│  VideoDubbing.Domain    Entities (Job, TranscriptSegment, ...)   │
│  VideoDubbing.Contracts DTOs / shared records                   │
├─────────────────────────────────────────────────────────────────┤
│  VideoDubbing.Infrastructure  EF Core, storage, providers,       │
│                                queue implementations, FFmpeg      │
└─────────────────────────────────────────────────────────────────┘
```

Running services:

| Service | Role |
|---|---|
| `VideoDubbing.Api` | Receives uploads, runs the in-process pipeline (zero-infra mode), serves artifacts. |
| `VideoDubbing.Worker` | Alternative consumer host used when messaging is externalized. |

Zero-infra mode (current live setup): `Database:Provider=Sqlite` + `Messaging:Provider=NoOp` with `NoOpProcessInProcess=true`. One API process does everything: upload → queue (in-process Task) → orchestrate → export → serve.

---

## 2. Code Organization

```
src/
  VideoDubbing.Domain/          Zero-dependency entities & enums
    Jobs/    Job, TranscriptSegment, Speaker, Artifact, ProcessingLog,
             JobStatus, ProcessingStage, JobMode
  VideoDubbing.Contracts/       Zero-dependency DTOs / records
  VideoDubbing.Application/     Pipeline/, Providers/ProviderContracts.cs,
             Jobs/JobService.cs, Configuration/Options.cs, Abstractions/
  VideoDubbing.Infrastructure/  Messaging/ (NoOp, MassTransit), Providers/,
             Persistence/ (EF Core), Storage/, Media/ (FFmpeg wrappers)
  VideoDubbing.Api/             Controllers/, appsettings.json, Program.cs
  VideoDubbing.Worker/          Hosted services / CLI consumer
tests/
  VideoDubbing.UnitTests/
  VideoDubbing.IntegrationTests/
config/            appsettings.sample.json, .env.sample
docs/              API.md, CONFIGURATION.md, postman collection
scripts/           publish.ps1, run-dev.ps1
.github/workflows/ ci.yml (build + test on ubuntu-latest)
```

`Directory.Build.props`: `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=false`.

---

## 3. Configuration Management

Single source of truth: **appsettings.json sections**, read by options classes in `VideoDubbing.Application/Configuration/Options.cs`.

| Section | Options class | What it controls |
|---|---|---|
| `Processing` | `ProcessingOptions` | Size/duration limits, concurrency, timeout, retry, rate limit |
| `Providers` | `ProviderOptions` | Provider chains, CLI paths, Voice IDs, fallback flags |
| `Storage` | `StorageOptions` | Local vs S3 storage |
| `Security` | `SecurityOptions` | API key, rate limiting |
| `Media` | `MediaOptions` | FFmpeg / ffprobe paths |
| `AiSecrets` | `AiSecretsOptions` | Provider API keys (empty = provider disabled) |
| `ElevenLabs` | `ElevenLabsOptions` | Dubbing-API polling behavior |
| `VideoLocalization` | `VideoLocalizationOptions` | Vendor-style localization API settings |
| `Database` / `Messaging` | (raw config) | Provider selection: `Sqlite`/`Postgres`, `NoOp`/`RabbitMq` |
| `ConnectionStrings` | | PostgreSQL, SQLite, RabbitMQ connection strings |

Key `Processing` values (current): `MaxUploadSizeMb=5000`, `MaxDurationMinutes=10`, `AllowedFormats=[mp4,mov,avi,mkv]`, `MaxConcurrentUploads=10`, `MaxConcurrentJobs=4`, `ProcessingTimeoutMinutes=120`, `RetryCount=3`, `QueueLength=100`, rate limit 100/min per 10 min.

Secrets are **not** committed; API keys live in `AiSecrets` in user/config env and are blank in the repo.

### Database `videodubbing.db` (live)
- EF Core SQLite at `src/VideoDubbing.Api/videodubbing.db`.
- `<job id>` work artifacts are **deleted after completion** (orchestrator `finally` block) — only exported media persists in `Storage`.

---

## 4. AI Model Abstraction

Providers are abstracted behind interfaces in `VideoDubbing.Application/Providers/ProviderContracts.cs` (`IProviderResolver`, `IDiarizationProvider`, `ISpeechToTextProvider`, `ITranslationProvider`, `IVoiceProvider`, `IVoiceProfileRegistry`, `ILipSyncEngine`).

`ProviderResolver` (Infrastructure) exposes **chains** — ordered lists with fallback:

| Capability | Chain (Primary → Fallbacks) |
|---|---|
| Speech-to-text | `Whisper` → `Vosk` → `Mock` |
| Translation | `DeepL` → `MyMemory` → `OpenAi` → `Mock` |
| Voice synthesis | `ElevenLabs` → `AzureSpeech` → `Coqui` → `OpenVoice` → `EdgeTts` → `GoogleTts` |
| Diarization | `FfmpegSilence` (no fallback) |
| LipSync | `Passthrough` |

Behavior:
- `EnableFallback=true` → resolver returns the whole chain; the step tries providers in order until one succeeds/times out.
- `EnableCostAwareRouting` (off in current config) would prepend a "cheap" provider for very short jobs (`CostAwareShortJobSeconds=120`).
- Name → implementation lookup via DI dictionary (throws `InvalidOperationException` for unknown providers).

### Whisper model cache
`ggml-base.bin` is cached locally (first run downloads, subsequent runs reuse).

---

## 5. Processing Workflow (The Pipeline)

Single pipeline, fixed order, driven by `PipelineOrchestrator` (`PipelineOrchestrator.cs`). Steps sorted by `ProgressPercent`.

| Step | File | Stage | Progress |
|---|---|---|---|
| ExtractAudio | ExtractAudioStep.cs | 2 | 10% |
| Diarize | DiarizeStep.cs | 3 | 25% |
| Transcribe | TranscribeStep.cs | 4 | 40% |
| Translate | TranslateStep.cs | 5 | 55% |
| Synthesize | SynthesizeStep.cs | 6 | 70% (→ up to 90% in-step) |
| SynchronizeAndExport | SynchronizeAndExportStep.cs | 7 | 90% |

(Also defined but not executed in the current flow: `GeneratingSubtitles=8`, `Muxing=9`.)

Orchestrator responsibilities (per job):
1. Load job; **skip if cancelled**.
2. Create temp work dir `%TEMP%\video-dubbing-work\<jobIdN>`.
3. `MarkProcessing()`, persist.
4. For each step: update stage/progress, publish `Starting <Step>` progress, run step with **retry loop** (`RetryCount`, exponential backoff `2^attempt` seconds), persist after each.
5. On success `MarkCompleted()` + progress 100%.
6. On `OperationCanceledException` (explicit cancel) → `MarkCancelled()`.
7. On other exception → `MarkFailed(msg)` + publish error.
8. `finally`: delete work dir.

**Global timeout**: linked CTS with `CancelAfter(ProcessingTimeoutMinutes)` (120 min). Cancellation is also checked between steps (`ThrowIfCancelled` reloads job status).

### SynthesizeStep — parallel synthesis (speed upgrade)
- `MaxConcurrentLanguages = 3` and `MaxConcurrentSegmentsPerLanguage = 2` (up to 6 concurrent TTS runs).
- Output filenames are GUID-based per segment per language → **no cross-task filename collisions**.
- Per-speaker reference clips are pre-extracted once.
- TeX line `lock (context.ClipsByLanguage)` guards the shared clips dictionary (thread-safe).
- Live progress: publishes "Synthesizing <lang>: <done>/<total>" every 5 segments.

### TranscriptNormalizer
Applies on read (JobService) and in TranscribeStep: collapses phrase repeats (e.g. repeated musical lyrics) while preserving turn structure — English Whisper transcripts of music-heavy videos produced duplicate lines; this filter cleans them.

---

## 6. Queue Architecture

Abstraction: `IJobQueue.EnqueueAsync(jobId, correlationId, ct)`.

Two implementations:

| Implementation | When | Behavior |
|---|---|---|
| `NoOpJobQueue` (live) | `Messaging:Provider=NoOp` | If `NoOpProcessInProcess=true`, uses `Task.Run` in-process with a DI scope to invoke `IPipelineOrchestrator` directly. **No message broker. All state in the single API process.** |
| `MassTransitJobQueue` | `Messaging:Provider=RabbitMq` | Publishes to RabbitMQ; `Worker` host consumes and runs the pipeline, enabling horizontal scaling. |

Concurrency: `MaxConcurrentJobs=4`.

### Known limitation (important)
The NoOp/in-process queue holds the running jobs in the API process memory. If the API is **restarted mid-job**, those jobs are stranded at `Status=Processing` and never resume — there is no job pickup/resume after restart. They must be manually reset (e.g. set `Status=Failed` in the DB). This is the single biggest operational gotcha in zero-infra mode.

---

## 7. Scalability Approach

- **Thread-concurrency in SynthesizeStep** (languages + segments parallel) reduces wall-clock time from ~28–41 min to an expected few minutes for typical videos.
- **`MaxConcurrentJobs`** bounds pipeline parallelism per host.
- **External messaging (RabbitMq + MassTransit)** moves the pipeline to the `Worker` host; a set of workers can be run to scale horizontally, each bounded by `MaxConcurrentJobs`.
- **Database** via PostgreSQL provider supports multiple hosts hitting the same storage (SQLite is single-host).
- **Storage** is swappable: `Local` (default) or S3/MinIO (`StorageOptions.S3`) for shared artifact storage across hosts.
- **Impediments**: fixed sequential pipeline steps (no per-step fan-out), in-process mode not scalable past one process, long synchronous TTS dominates wall time.

---

## 8. Fault Tolerance

| Mechanism | Where | Notes |
|---|---|---|
| Per-step retry | `PipelineOrchestrator` | `RetryCount=3`, exponential backoff (`2^attempt` s); transient provider/network failures retried in-place |
| Global deadline | Orchestrator CTS | `ProcessingTimeoutMinutes=120`; kills runaway jobs rather than hanging forever |
| Cancel support | `JobsController.cancel` / Dubbing API | Sets `Status=Cancelled`; orchestrator aborts between steps and won't start cancelled jobs |
| Progress persistence | Orchestrator | Stage/percent persisted to DB after each step so state survives at step boundaries |
| Error surfacing | `MarkFailed` + `ProcessingLog` | Errors visible via `GET {id}/logs` and job status |
| Not-resilient | NoOp queue restart | In-process jobs die with the process; manual DB cleanup needed (see §6) |

### Real incident log (diagnosis pattern)
Jobs “stuck” at Synthesize/Synchronize were traced to (a) genuinely slow sequential synthesis (28–41 min on a 115-segment × 3-language video), and (b) **orphaned jobs** created pre-restart. Marking orphans `Failed` re-queuing fresh uploads resolved them.

---

## 9. API Demonstration

Base: `http://localhost:5043` (Dev). Postman collection: `docs/postman/VideoDubbing.postman_collection.json`.

### Jobs — `routes/api/v1/jobs`
| Method & Path | Purpose |
|---|---|
| `POST /upload` | Multipart video upload + `mode` + `targetLanguages` → creates job, enqueues |
| `GET /` | List jobs (status, stages) |
| `GET /{jobId}` | Job details |
| `GET /{jobId}/transcript` | Transcript |
| `GET /{jobId}/transcript/speakers` | Speaker list |
| `GET /{jobId}/transcript/{language}` | Per-language transcript |
| `GET /{jobId}/source` | Source video |
| `GET /{jobId}/video` | Dubbed video (exported) |
| `GET /{jobId}/subtitles`, `/vtt` | Subtitle formats |
| `GET /{jobId}/audio` | Dubbed audio |
| `GET /{jobId}/logs` | Processing logs |
| `POST /{jobId}/retry` | Re-run |
| `POST /{jobId}/cancel` | Cancel job |
| `GET /{jobId}/events` | Progress events |
| `POST /localize` | Vendor-style localization (HeyGen/ElevenLabs/CAMB.AI) |

### ElevenLabs-style Dubbing — `routes/api/dubbing`
| Method & Path | Purpose |
|---|---|
| `POST /translate` | Multipart upload → `202 {dubbingId, targetLanguage, status:dubbing}`; map to internal job |
| `GET /{dubbingId}` | Vendor status-poll compatible |
| `GET /{dubbingId}/audio/{language}` | Streams mp3 |
| `GET /{dubbingId}/transcript/{language}?raw=true` | Transcript |
| `POST /music` | Music/image generation vs ElevenLabs (prompt, duration 3–600 s, forceInstrumental) |
| `DELETE /{dubbingId}` | Remove |

### Diagnostics
`GET /api/v1/diag/providers` → returns the configured chains + which secret keys are present.

### Example upload (PowerShell)
```powershell
curl.exe -s -X POST "http://localhost:5043/api/v1/jobs/upload" `
  -F "video=@video.mp4" -F "targetLanguage=es" -F "targetLanguage=fr"
```

---

## 10. Docker Deployment

**Status: not containerized.** The repo intentionally ships no `Dockerfile`/`docker-compose`; the zero-infra Sqlite+NoOp mode runs as plain processes (see `scripts/publish.ps1`, `scripts/run-dev.ps1`).

To containerize:
1. **Images**: multi-stage `Dockerfile` (SDK build → runtime copy of `publish/`).
2. **Two services**:
   - `api` — ASP.NET API (`VideoDubbing.Api`).
   - `worker` — consumer host for `Message` provider (scales horizontally).
3. **Broker + DB**: add depends-on `rabbitmq` and `postgres`, switch `Database:Provider=Postgres`, `Messaging:Provider=RabbitMq`; mount/env-var `AiSecrets` and `Storage`.
4. **Shared storage**: use S3/MinIO container instead of local disk.
5. **Time**: pipeline loops up to `ProcessingTimeoutMinutes=120` per job; configure generous health-start period, no readiness cutoffs that kill in-flight synthesis.

---

## 11. Challenges & Key Design Decisions

1. **Music repeats → garbage transcripts.** Repeated phrases in song-heavy videos duplicated Whisper output. Decision: `TranscriptNormalizer.CollapsePhraseRepeats` (turn-aware, phrase-run collapsing) applied at read + transcribe time.
2. **"Stuck" jobs.** Long video TTS looked frozen (no progress for 20+ min). Decisions: (a) live per-segment progress; (b) parallel languages/segments in SynthesizeStep; (c) documented orphan-recovery.
3. **Sequential synthesis too slow.** 115 segments × 3 languages once took 28–41 min. Decision: `MaxConcurrentLanguages=3` × `MaxConcurrentSegmentsPerLanguage=2`, GUID filenames to avoid collisions, thread-safe shared clip cache.
4. **Infrastructure-free demo.** Options: real RabbitMQ + Postgres OR zero-infra single-process. Decision: provider-abstracted everything (`NoOp`, `Sqlite`, `Local`) so one `dotnet run` demoes the full product; production swaps Messaging → RabbitMq, DB → Postgres, storage → S3.
5. **Restart orphaned jobs.** In-process queue jobs die with the API process. Decision: keep `NoOpProcessInProcess` for dev, but treat restarts as unsafe → documented manual recovery (mark orphans `Failed`, re-upload).
6. **Timeout vs hang.** Buffering/network hangs killed jobs silently. Decision: orchestrator-level deadline (`ProcessingTimeoutMinutes`) + per-step retry with backoff + explicit cancel path.
7. **Provider fallback.** One cloud day: silent failures. Decision: chain-based resolver (primary → fallbacks), `EnableFallback` toggle, cost-aware short-job routing hook.
8. **Secrets hygiene.** API keys never committed; blank in `AiSecrets`, supplied via env/user config.
9. **Not-implemented claims.** ARCHITECTURE.md describes features not yet in code (e.g. full restart-continuation, subtitles/muxing pipeline assets). This document is the source of truth for what exists today.

---

## Quick Navigation
- Endpoints & payloads: `docs/API.md`
- All config keys & defaults: `docs/CONFIGURATION.md`
- Concept intent: `ARCHITECTURE.md`
- Why-notes: `DESIGN_DECISIONS.md`
- Live ground truth of what shipped: this file