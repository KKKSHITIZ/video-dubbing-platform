# Design Decisions

This document records the key design decisions, their rationale, and the alternatives
considered.

---

## 1. Clean / Onion Architecture

**Decision:** Organize into `Domain → Application → Infrastructure → Api/Worker` layers.

**Why:** Long-term maintainability, testability, and provider-swappability. The Domain
carries no dependencies, so the core business rules are trivially unit-testable. The
Application defines ports (interfaces); Infrastructure provides adapters. Changing a
library (e.g. EF Core vs Dapper, MassTransit vs custom queue) touches only one layer.

**Alternatives:** Traditional N-tier (Controllers → Services → Repos) was rejected because it
couples business logic to frameworks and makes the pipeline harder to test in isolation.

---

## 2. Job-based asynchronous processing over synchronous request/response

**Decision:** Clients upload and immediately get a `JobId`; dubbing runs asynchronously on a
Worker.

**Why:** Dubbing is long-running and variable (potentially minutes of AI + media work).
Blocking an HTTP request would exhaust connections, hurt UX, and prevent retries/resume.
Asynchronous processing gives natural back-pressure, progress reporting, and horizontal
scalability.

---

## 3. Queue: MassTransit + RabbitMQ (with a NoOp in-process transport)

**Decision:** Standardize on MassTransit for its transport abstraction (which lets us swap
RabbitMQ for Azure Service Bus, SQS, InMemory, etc.), and add a `None` transport so the repo
runs with **zero external infrastructure**.

**Why:** Keeps real-world reliability (persistent queues, retries, redelivery) while honoring
the "no Docker / plain .NET subset" constraint. RabbitMQ is the default production transport.

---

## 4. Persisted state as source of truth, queue as delivery mechanism

**Decision:** Job state + artifacts live in the DB and object storage; the queue only moves
messages.

**Why:** If a worker dies mid-pipeline, the job is found in a non-terminal state and can be
resumed/retried. The queue never holds the system's truth, so no message loss = data loss.

---

## 5. Postgres default with SQLite and InMemory for tests/dev

**Decision:** `Database:Provider` selects provider: `Postgres` (default), `Sqlite` (local),
or `InMemory` (tests).

**Why:** Postgres is a robust default for production (relational integrity, JSONB, mature
tooling). SQLite lets developers run locally with a real relational DB and no server;
InMemory gives fast CI. Integration tests use **SQLite**, not InMemory, to catch real
relational-provider issues.

---

## 6. Config-driven everything (no hardcoded settings)

**Decision:** Providers, limits, timeouts, storage, DB, queue, security, and secrets are all
driven by configuration.

**Why:** The assignment stresses configuration management; operators must be able to change
behavior (esp. AI providers and limits) without rebuilding. This also lets the same binaries
run in dev and prod.

---

## 7. AI provider abstraction layer with runtime resolution

**Decision:** Three provider interfaces (`IDiarizationProvider`, `ISpeechToTextProvider`,
`ITranslationProvider`, `IVoiceProvider`) resolved at runtime by `IProviderResolver`.

**Why:** AI vendors change fast and pricing/quality differ. Abstracting them means:
- Adding a vendor = adding an implementation + config, no pipeline changes.
- Providers can be chained for **fallback** and **cost-aware routing**.

**What it enables:**
- **Fallback:** if the primary STT fails, a chain tries `fallbacks` in order.
- **Cost-aware routing:** short jobs (≤ `CostAwareShortJobSeconds`) can use a cheaper local
  provider injected ahead of the paid one for a threshold, cutting cost for trivially short
  clips without changing quality expectations for long ones.

---

## 8. Model/Provider selection strategy

**Decision:** Provide sensible defaults (quality) but allow cost optimization via chains and
a `cheap` provider slot per category.

**Why:** Balances default quality with the assignment's requirement to evaluate cost
alternatives for typical workloads. Real deployments tune per workload.

---

## 9. Per-step idempotent pipeline

**Decision:** Each pipeline step is a small class that skips already-completed work and
writes granular `ProcessingLog`/`Artifact` records.

**Why:** Enables safe **retry** (start where it left off) and clear step-level observability.
Individually testable steps also make the pipeline easy to extend.

---

## 10. Retry / Cancel are first-class job lifecycle operations

**Decision:** `Failed`/`Cancelled` jobs can be retried; pending/processing jobs can be
cancelled. Both are exposed via REST and validated by the Domain state machine.

**Why:** Real dubbing pipelines fail (transient AI timeouts, bad input). Operators must be
able to recover without re-uploading. Cancel protects users from runaway long jobs and
reclaims compute.

---

## 11. Storage behind an interface: Local FS + S3/MinIO

**Decision:** `IObjectStorage` with `LocalObjectStorage` and `S3ObjectStorage`.

**Why:** Keeps storage swappable and testable; enables the cheap local default while
supporting scalable object storage in production. All paths are normalized and
path-traversal is blocked.

---

## 12. Media processing behind `IMediaProcessor`

**Decision:** Media operations (probe, extract, mix, subtitle burn) go through one interface
with an FFmpeg implementation and a `TestMediaProcessor` stub.

**Why:** FFmpeg is an external dependency that may be absent; abstracting it lets integration
tests exercise the whole pipeline deterministically without it.

---

## 13. Value-generated `Guid` keys configured in EF

**Decision:** All entity `Guid` PKs are assigned `Guid.NewGuid()` in the Domain and configured
with `ValueGeneratedNever()`; `TargetLanguages` uses an explicit `ValueComparer`.

**Why:** `Guid.NewGuid()` PKs are never database-generated, so letting EF default to
`ValueGeneratedOnAdd` caused real insert/concurrency bugs on relational providers (a specific
SQLite bug observed during development where child inserts into `Include`d collections
reported "expected to affect 1 row but affected 0"). Being explicit removes ambiguity and
fixes correctness across all providers.

---

## 14. Structured logging + metrics + health + SSE progress

**Decision:** Serilog structured logs with `CorrelationId`; OpenTelemetry/Prometheus metrics;
`/health`; SSE progress stream.

**Why:** Operational visibility is required for a "production-ready" submission and for
debugging long pipelines.

---

## 15. No Docker (per requirement waiver)

**Decision:** No `Dockerfile`s or `docker-compose`.

**Why:** The user explicitly waived the Docker requirement in favor of a plain .NET solution
run in VS Code. The platform nonetheless scales to production purely via configuration and
process multiplicity (Postgres, RabbitMQ, S3 can be added externally).

---

## 16. Solution file

**Decision:** A standard `VideoDubbingPlatform.sln` (the original `.slnx` format wasn't
supported by SDK 8), containing all 8 projects.

**Why:** Lets SDK 8 `dotnet build` / `dotnet test` the whole solution from the CLI in VS Code.

---

## 17. Testing strategy

**Decision:** 65 unit tests (domain, application, pipeline, storage security, subtitles,
provider behavior) + 8 integration tests booting the real API host against SQLite with a
stubbed media processor. CI runs build + test.

**Why:** Fast unit-level confidence in business rules plus end-to-end confidence that the
full upload→pipeline→download path actually works through the real composition root.
