using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

public sealed class TranscribeStep : IPipelineStep
{
    private readonly IProviderResolver _providers;

    public TranscribeStep(IProviderResolver providers) => _providers = providers;

    public string Name => "Transcribe";
    public ProcessingStage Stage => ProcessingStage.Transcribing;
    public int ProgressPercent => 40;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var duration = context.Job.DurationSeconds ?? 0;
        Exception? last = null;
        foreach (var provider in _providers.SpeechToTextChain(duration))
        {
            try
            {
                var result = await provider.TranscribeAsync(
                    context.AudioPath,
                    context.SpeakerTurns,
                    context.Job.SourceLanguage,
                    cancellationToken);
                context.Job.DetectedLanguage = result.DetectedLanguage;
                context.Transcript = TranscriptNormalizer.CollapseRepeated(result.Turns).ToList();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null || context.Transcript.Count == 0)
        {
            throw last ?? new InvalidOperationException("Speech recognition produced an empty transcript.");
        }

        // Caption-only modes output captions in the SOURCE language, so the single "target" language
        // is the detected one — keeps the transcript/artifacts/UI consistent without any TTS keys.
        if (context.Job.ModeKind.IsCaptionOnly())
        {
            var captionLanguage = string.IsNullOrWhiteSpace(context.Job.DetectedLanguage) ? "en" : context.Job.DetectedLanguage;
            context.Job.TargetLanguages = [captionLanguage];
        }

        context.Job.Segments.Clear();
        var sequence = 0;
        foreach (var turn in context.Transcript)
        {
            var speaker = context.Job.Speakers.FirstOrDefault(s => s.Label == turn.SpeakerLabel);
            context.Job.Segments.Add(new TranscriptSegment
            {
                JobId = context.Job.Id,
                SpeakerId = speaker?.Id,
                Sequence = sequence++,
                StartSeconds = turn.StartSeconds,
                EndSeconds = turn.EndSeconds,
                SourceText = turn.Text,
                Language = context.Job.DetectedLanguage
            });
        }
    }
}
