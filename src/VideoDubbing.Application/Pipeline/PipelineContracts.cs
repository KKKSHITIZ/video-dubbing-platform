using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline;

public sealed class PipelineContext
{
    public required Job Job { get; init; }
    public required string WorkDirectory { get; init; }
    public string SourceVideoPath { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public List<Media.SpeakerTurn> SpeakerTurns { get; set; } = [];
    public List<Media.TranscriptTurn> Transcript { get; set; } = [];
    public Dictionary<string, List<Media.TranscriptTurn>> Translations { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<Media.SynthesizedClip>> ClipsByLanguage { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IPipelineStep
{
    string Name { get; }
    ProcessingStage Stage { get; }
    int ProgressPercent { get; }
    Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken);
}

public interface IPipelineOrchestrator
{
    Task ProcessAsync(Guid jobId, CancellationToken cancellationToken);
}
