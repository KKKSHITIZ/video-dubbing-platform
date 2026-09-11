# REST API Reference

Base URL: `http://localhost:5080` (dev) — see `launchSettings.json`.

All job endpoints are under the prefix `/api/v1/jobs`. Responses are JSON. Some downloads
return binary file streams.

Interactive docs: run the API and open `/swagger`.

---

## Authentication

Optional (config `Security:RequireApiKey`). When enabled, clients must send:

```
X-Api-Key: <key>
```

Requests without a valid key return `401`.

---

## POST /api/v1/jobs/upload

Upload a source video and enqueue dubbing into one or more target languages.

**Content-Type:** `multipart/form-data`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `video` | file | yes | Source video (mp4/mov/avi/mkv; ≤ `MaxUploadSizeMb`; ≤ `MaxDurationMinutes`) |
| `targetLanguages` | text | yes | Comma-separated target ISO language codes, e.g. `en,es,fr` |
| `sourceLanguage` | text | no | Source language code, or `auto` (default) for auto-detection |

**Responses**

- `202 Accepted`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Queued",
  "targetLanguages": ["en", "es"],
  "createdAt": "2026-09-07T10:00:00Z"
}
```

- `400 Bad Request` — missing file, unsupported format, file too large, video too long,
  queue full, or invalid target language list.

```json
{ "error": "Upload rejected: file type 'exe' is not allowed." }
```

> The URL of the accepted resource is returned in the `Location` header
> (`/api/v1/jobs/{jobId}`).

---

## GET /api/v1/jobs/{jobId}

Get the current processing status.

**Responses**

- `200 OK`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Processing",
  "stage": "Transcribing",
  "progressPercent": 40,
  "sourceLanguage": "auto",
  "detectedLanguage": "en",
  "targetLanguages": ["en", "es"],
  "errorMessage": null,
  "createdAt": "2026-09-07T10:00:00Z",
  "updatedAt": "2026-09-07T10:00:05Z",
  "startedAt": "2026-09-07T10:00:02Z",
  "completedAt": null
}
```

**Status values:** `Pending`, `Queued`, `Processing`, `Completed`, `Failed`, `Cancelled`.

- `404 Not Found` — unknown `jobId`.

---

## GET /api/v1/jobs/{jobId}/transcript

Get the transcript, detected speakers, and translations.

**Responses**

- `200 OK`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "detectedLanguage": "en",
  "speakers": [
    { "id": "…", "label": "Speaker 1", "speakingDurationSeconds": 42.5, "voiceId": "v1" }
  ],
  "segments": [
    {
      "sequence": 1,
      "speakerId": "…",
      "speakerLabel": "Speaker 1",
      "startSeconds": 0.0,
      "endSeconds": 2.4,
      "sourceText": "Hello everyone.",
      "translations": { "es": "Hola a todos.", "fr": "Bonjour à tous." }
    }
  ]
}
```

- `404 Not Found` — unknown `jobId`.

---

## GET /api/v1/jobs/{jobId}/video?language={language}

Download the dubbed video for a target language.

**Query parameters**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `language` | string | yes | Target language code for which artifacts exist |

**Responses**

- `200 OK` — binary video file (`Content-Disposition` with the file name).
- `404 Not Found` — no artifact for that language / job not completed.
- `400 Bad Request` — job not yet finished.

---

## GET /api/v1/jobs/{jobId}/subtitles?language={language}

Download SRT subtitles for a target language.

**Query parameters**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `language` | string | yes | Target language code |

**Responses**

- `200 OK` — SRT text file.
- `404 / 400` — as above.

---

## GET /api/v1/jobs/{jobId}/logs

Get the processing log entries for a job.

**Responses**

- `200 OK`

```json
[
  { "timestamp": "2026-09-07T10:00:02Z", "level": "Info", "stage": "Diarizing", "message": "Detected 2 speakers" },
  { "timestamp": "2026-09-07T10:00:03Z", "level": "Info", "stage": "Transcribing", "message": "Transcribed 8 segments" }
]
```

- `404 Not Found` — unknown `jobId`.

---

## POST /api/v1/jobs/{jobId}/retry

Re-queue a `Failed` or `Cancelled` job to run the pipeline again.

**Responses**

- `202 Accepted`

```json
{ "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "status": "Queued" }
```

- `400 Bad Request` — job is not in a retryable state.

---

## POST /api/v1/jobs/{jobId}/cancel

Cancel a `Pending` / `Queued` / `Processing` job and stop in-flight work.

**Responses**

- `202 Accepted`

```json
{ "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6", "status": "Cancelled" }
```

- `400 Bad Request` — job already in a terminal state.

---

## GET /api/v1/jobs/{jobId}/events

Server-Sent Events (SSE) live progress stream for a job.

- First message: the current job status.
- Subsequent `data:` frames: `JobProgressEvent` payloads as step progress is published.

```text
data: {"jobId":"…","status":"Processing","stage":"Transcribing","progressPercent":40}

data: {"jobId":"…","status":"Processing","stage":"Synthesizing","progressPercent":65}
```

---

## GET /health

Liveness/readiness probe. Returns `200 OK` when the app is healthy.

## GET /metrics

Prometheus-formatted metrics (OpenTelemetry exporter).
