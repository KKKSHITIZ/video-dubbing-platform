using VideoDubbing.Application.Providers;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

public sealed class DiarizeStep : IPipelineStep
{
    private readonly IProviderResolver _providers;

    public DiarizeStep(IProviderResolver providers) => _providers = providers;

    public string Name => "Diarize";
    public ProcessingStage Stage => ProcessingStage.Diarizing;
    public int ProgressPercent => 25;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var duration = context.Job.DurationSeconds ?? 0;
        Exception? last = null;
        foreach (var provider in _providers.DiarizationChain(duration))
        {
            try
            {
                context.SpeakerTurns = (await provider.DiarizeAsync(context.AudioPath, cancellationToken)).ToList();
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (last is not null || context.SpeakerTurns.Count == 0)
        {
            throw last ?? new InvalidOperationException("Diarization produced no speaker turns.");
        }

        var grouped = context.SpeakerTurns
            .GroupBy(t => t.SpeakerLabel)
            .Select(g => new Speaker
            {
                JobId = context.Job.Id,
                Label = g.Key,
                SpeakingDurationSeconds = g.Sum(t => Math.Max(0, t.EndSeconds - t.StartSeconds))
            })
            .ToList();

        context.Job.Speakers.Clear();
        foreach (var speaker in grouped)
        {
            context.Job.Speakers.Add(speaker);
        }
    }
}
