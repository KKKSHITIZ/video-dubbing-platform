using System.Text;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

/// <summary>
/// Final export, driven by the job's <see cref="JobMode"/> (Dub Studio-style composable modes):
/// <list type="bullet">
/// <item><b>Dub / Remix</b> — time-aligned synthesized track replaces the original audio.</item>
/// <item><b>VoiceOver</b> — synthesized track is mixed over the ducked original audio.</item>
/// <item><b>Subtitles</b> — original video (audio untouched) + source-language caption files.</item>
/// <item><b>Transcript</b> — no video at all, just transcript + caption files.</item>
/// </list>
/// Caption output honors the job's <see cref="SubtitleStyle"/> (None / Translated / Original).
/// </summary>
public sealed class SynchronizeAndExportStep : IPipelineStep
{
    private readonly IMediaProcessor _media;
    private readonly IObjectStorage _storage;
    private readonly IProviderResolver _providers;
    private readonly IJobProgressPublisher _progress;

    public SynchronizeAndExportStep(IMediaProcessor media, IObjectStorage storage, IProviderResolver providers, IJobProgressPublisher progress)
    {
        _media = media;
        _storage = storage;
        _providers = providers;
        _progress = progress;
    }

    public string Name => "SynchronizeAndExport";
    public ProcessingStage Stage => ProcessingStage.Synchronizing;
    public int ProgressPercent => 90;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var mode = context.Job.ModeKind;
        var languages = mode.IsCaptionOnly()
            ? (IReadOnlyList<string>)new[] { CaptionLanguage(context.Job) }
            : context.Job.TargetLanguages;

        foreach (var language in languages)
        {
            if (mode != JobMode.Transcript)
            {
                await _progress.PublishAsync(context.Job.Id, context.Job.Status, Stage, ProgressPercent, $"Exporting {language}", cancellationToken);
                await ExportVideoAsync(context, language, mode, cancellationToken);
            }
        }

        await ExportCaptionsAsync(context, mode, cancellationToken);

        var transcriptJson = BuildTranscriptJson(context);
        await SaveArtifactContentAsync(context, transcriptJson, "transcript", context.Job.DetectedLanguage, "application/json", cancellationToken);
    }

    private async Task ExportVideoAsync(PipelineContext context, string language, JobMode mode, CancellationToken cancellationToken)
    {
        var tempDubbed = Path.Combine(context.WorkDirectory, $"dubbed-{language}-tmp.mp4");
        var dubbedPath = Path.Combine(context.WorkDirectory, $"dubbed-{language}.mp4");

        switch (mode)
        {
            case JobMode.VoiceOver:
            {
                // Original drowned to -13 dB, synthesized voice on top (documentary style).
                var track = await _media.BuildAudioTrackAsync(context.ClipsByLanguage[language], context.WorkDirectory, cancellationToken);
                var mix = Path.Combine(context.WorkDirectory, $"voiceover-{language}.wav");
                await _media.MixVoiceOverAsync(context.AudioPath, track, mix, cancellationToken);
                await _media.MuxAudioWithVideoAsync(context.SourceVideoPath, mix, tempDubbed, cancellationToken);
                File.Move(tempDubbed, dubbedPath, overwrite: true);
                break;
            }
            case JobMode.Subtitles:
                // Same video, original audio — only caption files change.
                await _media.MuxAudioWithVideoAsync(context.SourceVideoPath, context.AudioPath, tempDubbed, cancellationToken);
                File.Move(tempDubbed, dubbedPath, overwrite: true);
                break;
            default:
            {
                var clips = context.ClipsByLanguage[language];
                var track = await _media.BuildAudioTrackAsync(clips, context.WorkDirectory, cancellationToken);
                var lipSync = _providers.ResolveLipSync();
                if (lipSync.Name.Equals("Passthrough", StringComparison.OrdinalIgnoreCase))
                {
                    await _media.MuxAudioWithVideoAsync(context.SourceVideoPath, track, tempDubbed, cancellationToken);
                    File.Move(tempDubbed, dubbedPath, overwrite: true);
                }
                else
                {
                    var result = await lipSync.LipSyncAsync(context.SourceVideoPath, track, tempDubbed, language, cancellationToken);
                    if (result != tempDubbed || !File.Exists(tempDubbed))
                    {
                        if (File.Exists(tempDubbed))
                        {
                            File.Copy(tempDubbed, dubbedPath, overwrite: true);
                        }
                        else
                        {
                            await _media.MuxAudioWithVideoAsync(context.SourceVideoPath, track, dubbedPath, cancellationToken);
                        }
                    }
                    else
                    {
                        File.Move(tempDubbed, dubbedPath, overwrite: true);
                    }
                }

                break;
            }
        }

        await SaveArtifactAsync(context, dubbedPath, "video", language, "video/mp4", cancellationToken);
    }

    private async Task ExportCaptionsAsync(PipelineContext context, JobMode mode, CancellationToken cancellationToken)
    {
        var style = context.Job.SubtitleStyleKind;
        if (style == SubtitleStyle.None)
        {
            return;
        }

        if (style == SubtitleStyle.Original || mode.IsCaptionOnly())
        {
            // Source-language captions, one set at the detected language.
            var language = CaptionLanguage(context.Job);
            await SaveCaptionsAsync(context, language, context.Transcript, cancellationToken);
            return;
        }

        foreach (var language in context.Job.TargetLanguages)
        {
            if (context.Translations.TryGetValue(language, out var translated))
            {
                await SaveCaptionsAsync(context, language, translated, cancellationToken);
            }
        }
    }

    private async Task SaveCaptionsAsync(PipelineContext context, string language, IReadOnlyList<TranscriptTurn> turns, CancellationToken cancellationToken)
    {
        var srt = SubtitleFormatter.BuildSrt(turns);
        var vtt = SubtitleFormatter.BuildVtt(turns);
        await SaveArtifactContentAsync(context, srt, "subtitles", language, "application/x-subrip", cancellationToken);
        await SaveArtifactContentAsync(context, vtt, "vtt", language, "text/vtt", cancellationToken);
    }

    private static string CaptionLanguage(Job job)
        => string.IsNullOrWhiteSpace(job.DetectedLanguage) ? "en" : job.DetectedLanguage;

    private static string BuildTranscriptJson(PipelineContext context)
    {
        var clipsJson = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in context.Job.TargetLanguages)
        {
            if (!context.ClipsByLanguage.TryGetValue(language, out var clips))
            {
                continue;
            }

            clipsJson[language] = clips
                .OrderBy(c => c.StartSeconds)
                .Select(c => new
                {
                    Speaker = c.SpeakerLabel,
                    StartSeconds = c.TargetStartSeconds,
                    EndSeconds = c.TargetEndSeconds,
                    AlignmentSpeed = c.AlignmentSpeed,
                    AudioPath = System.IO.Path.GetFileName(c.AudioPath)
                })
                .Select(x => (object)x)
                .ToList();
        }

        var segments = context.Job.Segments
            .OrderBy(s => s.Sequence)
            .Select(s => new
            {
                s.Sequence,
                s.StartSeconds,
                s.EndSeconds,
                s.SourceText,
                Translations = s.TranslationsJson
            });

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            context.Job.Id,
            context.Job.DetectedLanguage,
            context.Job.TargetLanguages,
            context.Job.Mode,
            Speakers = context.Job.Speakers.Select(s => new
            {
                s.Label,
                VoiceId = s.VoiceId
            }),
            Segments = segments,
            AlignedAudioClips = clipsJson
        });
    }

    private async Task SaveArtifactAsync(PipelineContext context, string filePath, string kind, string language, string contentType, CancellationToken cancellationToken)
    {
        var key = $"jobs/{context.Job.Id:N}/{kind}-{language}{Path.GetExtension(filePath)}";
        await using (var stream = File.OpenRead(filePath))
        {
            await _storage.SaveAsync(stream, key, contentType, cancellationToken);
        }

        context.Job.Artifacts.Add(new Artifact
        {
            JobId = context.Job.Id,
            Kind = kind,
            Language = language,
            StorageKey = key,
            ContentType = contentType,
            SizeBytes = new FileInfo(filePath).Length
        });
    }

    private async Task SaveArtifactContentAsync(PipelineContext context, string content, string kind, string language, string contentType, CancellationToken cancellationToken)
    {
        var extension = kind switch
        {
            "subtitles" => ".srt",
            "vtt" => ".vtt",
            _ => ".json"
        };
        var key = $"jobs/{context.Job.Id:N}/{kind}-{language}{extension}";
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes);
        await _storage.SaveAsync(stream, key, contentType, cancellationToken);

        context.Job.Artifacts.Add(new Artifact
        {
            JobId = context.Job.Id,
            Kind = kind,
            Language = language,
            StorageKey = key,
            ContentType = contentType,
            SizeBytes = bytes.Length
        });
    }
}