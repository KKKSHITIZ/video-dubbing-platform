using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

public sealed class SynthesizeStep : IPipelineStep
{
    private const int MaxConcurrentSegmentsPerLanguage = 2;
    private const int MaxConcurrentLanguages = 3;

    private readonly IProviderResolver _providers;
    private readonly IVoiceProfileRegistry _voiceProfiles;
    private readonly IMediaProcessor _media;
    private readonly IJobProgressPublisher _progress;

    public SynthesizeStep(
        IProviderResolver providers,
        IVoiceProfileRegistry voiceProfiles,
        IMediaProcessor media,
        IJobProgressPublisher progress)
    {
        _providers = providers;
        _voiceProfiles = voiceProfiles;
        _media = media;
        _progress = progress;
    }

    public string Name => "Synthesize";
    public ProcessingStage Stage => ProcessingStage.Synthesizing;
    public int ProgressPercent => 70;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        // Caption-only modes (Subtitles / Transcript) keep the original audio — no voice work.
        if (context.Job.ModeKind.IsCaptionOnly())
        {
            return;
        }

        var languages = context.Job.TargetLanguages.ToArray();
        var languageTotal = languages.Length;

        // Reference clips are extracted once per speaker up front so the voice-cloning
        // seed is ready (and immune to races) before segments are synthesized in parallel.
        var referenceBySpeaker = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var speaker in context.Job.Speakers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            referenceBySpeaker[speaker.Label] = await EnsureReferenceClipAsync(context, speaker.Label, cancellationToken);
        }

        // All target languages are independent: dub them in parallel (up to 3 languages,
        // each up to 2 concurrent segments) so TTS network latency overlaps.
        await Parallel.ForEachAsync(
            languages,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentLanguages, CancellationToken = cancellationToken },
            async (language, token) =>
            {
                var clips = await SynthesizeLanguageAsync(context, language, referenceBySpeaker, token);
                lock (context.ClipsByLanguage)
                {
                    context.ClipsByLanguage[language] = clips;
                }
            });

        var languageIndex = 0;
        foreach (var language in languages)
        {
            languageIndex++;
            await PublishLanguageProgressAsync(context, languageIndex, languageTotal, token: cancellationToken);
        }
    }

    private async Task<List<SynthesizedClip>> SynthesizeLanguageAsync(
        PipelineContext context,
        string language,
        IReadOnlyDictionary<string, string?> referenceBySpeaker,
        CancellationToken cancellationToken)
    {
        var turns = context.Translations[language];
        var clips = new List<SynthesizedClip>(turns.Count);
        var clipLock = new object();
        var segmentDone = 0;

        await Parallel.ForEachAsync(
            turns,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSegmentsPerLanguage, CancellationToken = cancellationToken },
            async (turn, token) =>
            {
                var clip = await SynthesizeClipAsync(
                    context,
                    language,
                    turn,
                    referenceBySpeaker.TryGetValue(turn.SpeakerLabel, out var refPath) ? refPath : null,
                    token);

                lock (clipLock)
                {
                    clips.Add(clip);
                }

                var done = Interlocked.Increment(ref segmentDone);
                if (done == turns.Count || done % 5 == 0)
                {
                    await _progress.PublishAsync(
                        context.Job.Id,
                        context.Job.Status,
                        Stage,
                        ProgressPercent + Math.Min(20, done * 20 / Math.Max(1, turns.Count)),
                        $"Synthesizing {language}: {Math.Min(done, turns.Count)}/{turns.Count}",
                        token);
                }
            });

        // Clips are produced in completion order; time-align them for the exporter.
        clips.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return clips;
    }

    private async Task<SynthesizedClip> SynthesizeClipAsync(
        PipelineContext context,
        string language,
        Media.TranscriptTurn turn,
        string? referencePath,
        CancellationToken cancellationToken)
    {
        var speaker = context.Job.Speakers.FirstOrDefault(sp => sp.Label == turn.SpeakerLabel);
        var voiceId = _voiceProfiles.ResolveVoiceId(speaker?.Id ?? Guid.Empty, turn.SpeakerLabel);
        if (speaker is not null)
        {
            speaker.VoiceId = voiceId;
        }

        var path = Path.Combine(context.WorkDirectory, $"{language}_{Guid.NewGuid():N}.wav");
        var targetDuration = Math.Max(0.2, turn.EndSeconds - turn.StartSeconds);
        Exception? last = null;

        foreach (var provider in _providers.VoiceChain(context.Job.DurationSeconds ?? 0))
        {
            try
            {
                await provider.SynthesizeAsync(turn.Text, turn.SpeakerLabel, language, path, targetDuration, cancellationToken, referencePath);
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null || !File.Exists(path))
        {
            throw last ?? new InvalidOperationException("Voice synthesis failed.");
        }

        // Timing alignment: stretch the generated clip to the exact original timestamp bounds.
        var alignedPath = Path.Combine(context.WorkDirectory, $"aligned-{language}_{Guid.NewGuid():N}.wav");
        var probe = await _media.ProbeAsync(path, cancellationToken);
        var actualDuration = probe.DurationSeconds > 0 ? probe.DurationSeconds : targetDuration;
        await _media.TimeStretchAsync(path, turn.EndSeconds, turn.StartSeconds, alignedPath, cancellationToken);

        return new SynthesizedClip(
            turn.SpeakerLabel,
            turn.StartSeconds,
            alignedPath,
            targetDuration,
            turn.StartSeconds,
            turn.EndSeconds,
            Math.Round(actualDuration / Math.Max(0.05, targetDuration), 3));
    }

    private async Task PublishLanguageProgressAsync(PipelineContext context, int languageIndex, int languageTotal, CancellationToken token)
    {
        await _progress.PublishAsync(
            context.Job.Id,
            context.Job.Status,
            Stage,
            ProgressPercent + Math.Min(20, languageIndex * 20 / Math.Max(1, languageTotal)),
            $"Synthesized {languageIndex}/{languageTotal} languages",
            token);
    }

    /// <summary>
    /// Cuts a short reference clip (~first occurrence of the speaker) used for voice cloning so the
    /// target voice matches the original speaker's timbre across the whole project.
    /// </summary>
    private async Task<string?> EnsureReferenceClipAsync(PipelineContext context, string speakerLabel, CancellationToken cancellationToken)
    {
        var firstTurn = context.Transcript.FirstOrDefault(t => t.SpeakerLabel == speakerLabel);
        if (firstTurn is null)
        {
            return null;
        }

        var path = Path.Combine(context.WorkDirectory, $"ref-{speakerLabel}.wav");
        if (File.Exists(path))
        {
            return path;
        }

        await _media.ExtractAudioSegmentAsync(context.AudioPath, firstTurn.StartSeconds, firstTurn.EndSeconds, path, cancellationToken);
        return File.Exists(path) ? path : null;
    }
}