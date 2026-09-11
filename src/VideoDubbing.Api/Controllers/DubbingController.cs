using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Localization;
using VideoDubbing.Application.Media;
using VideoDubbing.Infrastructure.Localization;

namespace VideoDubbing.Api.Controllers;

/// <summary>
/// High-fidelity dubbing through the ElevenLabs Dubbing API (POST /v1/dubbing). The vendor engine
/// transcribes/translates/synthesizes/mixes server-side, so the returned audio track preserves the
/// original video's duration and pacing exactly.
/// Configure the key via <c>ElevenLabs:ApiKey</c> (or <c>AiSecrets:ElevenLabsApiKey</c>).
/// </summary>
[ApiController]
[Route("api/dubbing")]
public sealed class DubbingController : ControllerBase
{
    // 5000 MB cap, matches Processing.MaxUploadSizeMb (Kestrel + multipart limits are configured from it).
    private const long MaxRequestBytes = 5_242_880_000;

    private readonly ElevenLabsDubbingApi _dubbing;
    private readonly ElevenLabsMusicApi _music;
    private readonly IMediaProcessor _media;
    private readonly ProcessingOptions _processing;

    public DubbingController(
        ElevenLabsDubbingApi dubbing,
        ElevenLabsMusicApi music,
        IMediaProcessor media,
        IOptions<ProcessingOptions> processing)
    {
        _dubbing = dubbing;
        _music = music;
        _media = media;
        _processing = processing.Value;
    }

    /// <summary>Upload a video and start an ElevenLabs dubbing job for one target language.</summary>
    [HttpPost("translate")]
    [RequestSizeLimit(MaxRequestBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Translate(
        IFormFile file,
        [FromForm] string targetLanguage,
        [FromForm] string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "A video file is required." });
        }

        var target = targetLanguage?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(target))
        {
            return BadRequest(new { error = "A target language is required (e.g. targetLanguage=fr)." });
        }

        var maxSeconds = _processing.MaxDurationMinutes * 60;
        var tempCopy = Path.Combine(Path.GetTempPath(), $"dubbing-probe-{Guid.NewGuid():N}.{Path.GetExtension(file.FileName)}");
        try
        {
            await using (var stream = file.OpenReadStream())
            {
                await using var targetFile = new FileStream(tempCopy, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await stream.CopyToAsync(targetFile, cancellationToken);
            }

            var probe = await _media.ProbeAsync(tempCopy, cancellationToken);
            if (probe.DurationSeconds > maxSeconds)
            {
                return BadRequest(new { error = $"Video exceeds maximum duration of {_processing.MaxDurationMinutes} minutes." });
            }

            if (!probe.HasAudio)
            {
                return BadRequest(new { error = "Video does not contain an audio track." });
            }

            var source = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage.Trim();
            await using var upload = System.IO.File.OpenRead(tempCopy);
            var result = await _dubbing.CreateAsync(
                upload,
                file.FileName,
                file.ContentType,
                source,
                target,
                cancellationToken);

            return Accepted(new { dubbingId = result.DubbingId, targetLanguage = target, status = "dubbing" });
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempCopy))
                {
                    System.IO.File.Delete(tempCopy);
                }
            }
            catch
            {
                // probe temp file cleanup is best-effort
            }
        }
    }

    /// <summary>Poll the ElevenLabs dubbing status.</summary>
    [HttpGet("{dubbingId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(string dubbingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dubbingId))
        {
            return BadRequest(new { error = "A dubbing id is required." });
        }

        var status = await _dubbing.GetStatusAsync(dubbingId, cancellationToken);
        return Ok(new
        {
            dubbingId = status.DubbingId,
            status = status.Status,
            error = status.Error,
            targetLanguages = status.TargetLanguages
        });
    }

    /// <summary>Download the finished dubbed audio track for a language.</summary>
    [HttpGet("{dubbingId}/audio/{language}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadAudio(string dubbingId, string language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dubbingId) || string.IsNullOrWhiteSpace(language))
        {
            return BadRequest(new { error = "A dubbing id and language are required." });
        }

        var (stream, contentType, fileName) = await _dubbing.DownloadAudioAsync(dubbingId, language.Trim().ToLowerInvariant(), cancellationToken);
        return File(stream, contentType, fileName);
    }

    /// <summary>Download the translated transcript for a language, grouped speaker-by-speaker.
    /// Pass <c>?raw=true</c> for the unmodified vendor JSON.</summary>
    [HttpGet("{dubbingId}/transcript/{language}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadTranscript(
        string dubbingId,
        string language,
        [FromQuery] bool raw = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dubbingId) || string.IsNullOrWhiteSpace(language))
        {
            return BadRequest(new { error = "A dubbing id and language are required." });
        }

        var lang = language.Trim().ToLowerInvariant();
        if (raw)
        {
            var transcript = await _dubbing.DownloadTranscriptAsync(dubbingId, lang, cancellationToken);
            return File(System.Text.Encoding.UTF8.GetBytes(transcript), "application/json", $"transcript-{lang}.json");
        }

        var segments = await _dubbing.DownloadTranscriptSegmentsAsync(dubbingId, lang, cancellationToken);
        return Ok(new
        {
            dubbingId,
            language = lang,
            languageName = LanguageNames.DisplayName(lang),
            speakerSections = segments
                .GroupBy(s => s.Speaker)
                .Select(g => new
                {
                    speaker = g.Key,
                    startSeconds = Math.Round(g.Min(s => s.StartSeconds), 3),
                    endSeconds = Math.Round(g.Max(s => s.EndSeconds), 3),
                    segments = g.Select(s => new
                    {
                        start = Math.Round(s.StartSeconds, 3),
                        end = Math.Round(s.EndSeconds, 3),
                        text = s.Text
                    })
                }),
            segments = segments.Select(s => new
            {
                speaker = s.Speaker,
                start = Math.Round(s.StartSeconds, 3),
                end = Math.Round(s.EndSeconds, 3),
                text = s.Text
            })
        });
    }

    /// <summary>Compose an original music / SFX track from a prompt via the ElevenLabs Music API.</summary>
    [HttpPost("music")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ComposeMusic(
        [FromForm] string prompt,
        [FromForm] int? durationSeconds,
        [FromForm] string? modelId,
        [FromForm] bool forceInstrumental = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return BadRequest(new { error = "A prompt is required (e.g. prompt='A fast-paced electronic track for a video game')." });
        }

        var seconds = Math.Clamp(durationSeconds ?? 30, 3, 600);
        var (stream, contentType, fileName) = await _music.ComposeAsync(
            prompt.Trim(),
            seconds,
            modelId,
            forceInstrumental,
            cancellationToken);
        return File(stream, contentType, fileName);
    }

    /// <summary>Delete a dubbing job from ElevenLabs storage.</summary>
    [HttpDelete("{dubbingId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string dubbingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dubbingId))
        {
            return BadRequest(new { error = "A dubbing id is required." });
        }

        await _dubbing.DeleteAsync(dubbingId, cancellationToken);
        return NoContent();
    }
}