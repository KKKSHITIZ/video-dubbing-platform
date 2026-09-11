# Architecture

This document describes the high-level architecture of the **AI-Powered Video Dubbing
Platform**, the processing pipeline, the queue-based scaling model, failure recovery, and
how the components fit together.

---

## 1. System Overview

The platform is an **asynchronous, job-based, event-driven** system. Clients upload a video
and receive a `JobId`. The actual (potentially long-running) dubbing work happens
**asynchronously** on background workers, so the API stays responsive and can scale
independently of processing.

```
                    ┌────────────────────────────────────────────────────────┐
                    │                     Clients                             │
                    │     (dashboards, mobile, SDK, REST tools, Postman)      │
                    └──────────────────────────┬─────────────────────────────┘
                                               │ HTTPS (REST / SSE)
                    ┌──────────────────────────▼─────────────────────────────┐
                    │                     API (ASP.NET Core)                  │
                    │  ─ upload/validate ─ job lifecycle ─ status ─ metrics   │
                    └──────────────┬──────────────────────────┬──────────────┘
                                   │ writes/reads             │ enqueues
                    ┌──────────────▼──────────┐    ┌──────────▼──────────────┐
                    │  Metadata store (EF)    │    │  Job queue               │
                    │  DubbingDbContext        │    │  MassTransit +          │
                    │  Postgres / Sqlite       │    │  RabbitMQ (or NoOp)     │
                    └──────────────┬──────────┘    └──────────┬──────────────┘
                                   │                            │ dequeue
                    ┌──────────────▼────────────────────────────▼─────────────┐
                    │                     Worker (background)                  │
                    │              Processes job queue, runs pipeline          │
                    └──────────────┬──────────────────────────┬──────────────┘
                                   │                          │
                    ┌──────────────▼──────────┐    ┌──────────▼──────────────┐
                    │  Object storage          │    │   AI provider layer     │
                    │  Local FS / S3 / MinIO   │    │  diarize · transcribe · │
                    │                          │    │  translate · synthesize │
                    └──────────────────────────┘    └─────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| **API** | Accepts uploads, validates input, persists job metadata, enqueues jobs, exposes job lifecycle, streams progress, exposes metrics/health. |
| **Worker** | Consumes queued jobs and executes the dubbing pipeline (diarization → transcription → translation → voice synthesis → audio mux → subtitle export). |
| **Metadata store** | EF Core `DubbingDbContext`. Stores jobs, speakers, transcript segments, artifacts, audit logs, processing logs. |
| **Object storage** | Stores raw uploads and output artifacts (audio, video, subtitles, JSON). Local FS or S3/MinIO. |
| **Job queue** | Decouples API from processing. MassTransit over RabbitMQ; a `None` (in-process) transport for local/dev. |
| **AI provider layer** | Abstraction over diarization, STT, translation, and voice providers with runtime resolution, chains (fallback), and cost-aware routing. |

---

## 2. Clean Architecture / Onion layout

Dependencies point **inward**; the outer layers know about the inner ones, never the reverse.

```
        ┌───────────────────────────────────────────┐
        │  Api · Worker          (composition roots) │
        │  read config, wire DI, expose endpoints    │
        ├───────────────────────────────────────────┤
        │  Infrastructure  (implementations)         │
        │  EF Core · storage · queue · providers     │
        ├───────────────────────────────────────────┤
        │  Application     (use cases + pipeline)    │
        │  ports/interfaces define the contracts     │
        ├───────────────────────────────────────────┤
        │  Domain          (entities + state machine)│
        └───────────────────────────────────────────┘
```

- **Domain** — `Job`, `Speaker`, `TranscriptSegment`, `Artifact`, `JobStatus` enum, the
  allowed state transitions (e.g. `Pending → Processing → Completed/Failed`). No external
  dependencies; the job is the unit of truth about the workflow.
- **Application** — interfaces (ports) for `IJobRepository`, `IObjectStorage`,
  `IMediaProcessor`, `IJobQueue`, `IProviderResolver`, and the pipeline steps; implements the
  `JobService` use cases and the `PipelineOrchestrator`.
- **Infrastructure** — the concrete EF Core DbContext, local/S3 storage, MassTransit queue,
  and provider implementations.
- **Api/Worker** — composition roots that build the DI container from configuration.

---

## 3. The Dubbing Pipeline

The worker orchestrates a sequence of steps. Each step records `ProcessingLog` entries,
updates `JobStatus`, and writes `Artifact`s on success.

```
 Upload ──► Queued(Pending)
   │
   ▼
 ┌──────────────────────────────────────────────────────────────────────┐
 │ 1. Probe        detect codec/duration/language / speaker count       │
 │ 2. Extract      extract audio track → audio artifact                 │
 │ 3. Diarization  detect speakers + time boundaries (per speaker)      │
 │ 4. Transcribe   (STT) per speaker + timestamps → segments            │
 │ 5. Translate    per target language, preserving speaker/tone         │
 │ 6. Synthesize   per speaker voice → audio clips per segment          │
 │ 7. Mix/Mux      place synthesized audio at original timestamps       │
 │ 8. Export       build dubbed video + SRT subtitles per language      │
 └──────────────────────────────────────────────────────────────────────┘
   │
   ▼
 Completed (or Failed → retryable) → artifacts available for download
```

| Step | Output |
|------|--------|
| Probe / Extract | audio `Artifact` |
| Diarization | `Speaker` records |
| Transcription | `TranscriptSegment` records |
| Translation | translated segment text |
| Synthesis | per-segment voice clips |
| Mux/Mix | dubbed video `Artifact` |
| Export | SRT subtitles + JSON transcript `Artifact`s |

The orchestration lives in `VideoDubbing.Application/Pipeline`. Each step is a small,
individually-testable class (`ProbeStep`, `ExtractAudioStep`, `DiarizeStep`,
`TranscribeStep`, `TranslateStep`, `SynthesizeStep`, `MuxAudioStep`, `ExportStep`). Steps
guard against re-runs and support idempotent continuation (used by **retry**).

---

## 4. Job Lifecycle & State Machine

```
                    ┌────────────┐
                    │   Upload   │
                    └─────┬──────┘
                          ▼
                      ┌─────────┐  enqueue   ┌──────────┐  dequeue   ┌────────────┐
  ┌─────────┐  ──►   │ Pending │ ─────────► │ Queued   │ ─────────► │ Processing │
  │ Created │        └─────────┘            └──────────┘            └─────┬──────┘
  └─────────┘                                                             │
        ┌────────────────────────────────────────────────────────────────┴───┐
        │  success                                                           │ failure
        ▼                                                                     ▼
  ┌────────────┐                                                        ┌─────────┐
  │ Completed  │                                                        │ Failed  │──► Retry
  └────────────┘                                                        └─────────┘
        ▲                                                                   │
        │  cancel                                                           ▼
        └─────────┐                                                   ┌───────────┐
              ┌──────────┐   cancel                                  │ Cancelled │
              │ Processing│ ─────────────────────────────────────────►└───────────┘
              └──────────┘
```

Transitions are validated in the Domain (`Job.TransitionTo`, `Job.Cancel`, `Job.Retried`).
Invalid transitions throw a domain exception. `Failed`/`Cancelled` jobs can be **retried**,
which re-queues them and re-runs the pipeline from the earliest incomplete step.

---

## 5. Queue & Scalability

### 5.1 Queue abstraction

- Interface: `IJobQueue`.
- **Default**: MassTransit + RabbitMQ (`Messaging:Provider=RabbitMq`).
- **Dev**: `Messaging:Provider=None` + an in-process single-producer/consumer bridge so the
  repo runs with zero external services.

The API **enqueues** a job after a successful upload; the Worker **consumes** it.

### 5.2 Scaling model

Because the API and Worker are **separate processes**, they scale independently:

- **API scale-out**: more instances behind a load balancer; all share the DB and queue.
- **Worker scale-out**: run more workers to increase throughput — each competes for queued
  jobs, giving natural horizontal scaling and parallel processing.
- **Queue length control**: `Processing:MaxQueuedJobs` guardrails + `Processing:MaxConcurrency`;
  uploads are rejected when the queue is full (graceful back-pressure instead of unbounded growth).
- **Partition/retry**: MassTransit's retry + redelivery handles transient failures before they
  surface as job failures.

Because file **artifacts live in object storage** (not in-process memory) and **job state in
the DB**, any worker can resume/re-drive any job — the system is resilient to worker
restarts and can be scaled arbitrarily.

---

## 6. Fault Tolerance & Consistent Design

- **Persisted state first**: the job and its artifacts are the source of truth; the queue is
  a delivery mechanism, not a store of truth. A job that crashes mid-pipeline is found in a
  non-terminal state and can be resumed/retried.
- **Per-step idempotency**: steps skip work already done, enabling safe retries.
- **MediaProcessor** is behind an interface; a **TestMediaProcessor** stub drives integration
  tests without FFmpeg.
- **Provider fallback chains**: `ProviderResolver` tries configured providers in order
  (`primary` → `fallbacks`) when the active one fails or is unavailable.
- **Timeouts & concurrency guards**: pipeline step timeouts, job timeouts, `MaxConcurrency`,
  and `MaxQueuedJobs`.
- **Cleanup**: `CancelComputations` stops in-flight processing; orphaned/cancelled jobs are
  handled gracefully.
- **Path-traversal protection** and strict validation on all external inputs.

---

## 7. Observability

- **Health** — `/health` (app + DB + storage + queue readiness).
- **Metrics** — OpenTelemetry metrics exposed at `/metrics` (Prometheus format): request
  counts/durations, job durations, queue depth, provider call counts.
- **Logs** — Serilog structured logging with `CorrelationId` on each job.
- **Progress** — Server-Sent Events stream at `/jobs/{id}/events` so dashboards can show
  live step progress.

---

## 8. Security

- File validation: allowed extensions, MIME sniffing, max size, max duration.
- **Path-traversal protection** across all storage operations.
- **Rate limiting** (fixed-window, configurable) to protect upload endpoints.
- Optional **API-key** authentication for machine-to-machine clients.
- Secret management via environment variables / user-secrets / secret managers.
- ASP.NET Core defaults: HTTPS redirection (dev), CORS restricted via config.

---

## 9. Deployment (non-Docker)

The repository intentionally has **no Docker** deliverables (waiver). Two deployment modes:

1. **Single-node local**: API + Worker as two .NET processes, SQLite + NoOp queue + Local
   storage + local/Mock providers. Zero external infra. *(Best for demo & review.)*
2. **Production-like**: API + N Workers behind a load balancer, PostgreSQL, RabbitMQ,
   S3/MinIO, and cloud/local AI providers. Deploy each project as a self-contained .NET
   service on any Linux/Windows host or a managed runner.

Deployment is configuration-only — the same binaries run in both modes.

---

## 10. Diagrams

ASCII diagrams throughout this doc; a more detailed sequence:

```
Client            API               Queue          Worker          Storage         Providers
  │  upload        │                  │              │               │                │
  │───────────────►│ validate+persist  │              │               │                │
  │                │─────────────────►│              │               │                │
  │  200 JobId     │◄─────────────────│              │               │                │
  │◄───────────────│                  │  dequeue     │               │                │
  │                │                  │─────────────►│               │                │
  │                │                  │              │── probe/extract───────────────►│
  │                │                  │              │  ~diarize~  │                 │
  │                │                  │              │  transcribe  │◄───────────────┤
  │                │                  │              │  translate   │                 │
  │                │                  │              │  synthesize  │                │
  │                │                  │              │  mux+export  │───write───────►│
  │  poll status   │                  │              │               │                │
  │───────────────►│  read status     │              │               │                │
  │◄───────────────│                  │              │               │                │
  │  download      │───────────────────────────────►│   read artifact                 │
```

See [`DESIGN_DECISIONS.md`](DESIGN_DECISIONS.md) for rationale behind each architectural
choice.
