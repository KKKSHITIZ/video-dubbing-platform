using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

public sealed class ExtractAudioStep : IPipelineStep
{
    private readonly IObjectStorage _storage;
    private readonly IMediaProcessor _media;

    public ExtractAudioStep(IObjectStorage storage, IMediaProcessor media)
    {
        _storage = storage;
        _media = media;
    }

    public string Name => "ExtractAudio";
    public ProcessingStage Stage => ProcessingStage.ExtractingAudio;
    public int ProgressPercent => 10;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        context.SourceVideoPath = Path.Combine(context.WorkDirectory, "source" + Path.GetExtension(context.Job.StorageKey));
        await using (var input = await _storage.OpenReadAsync(context.Job.StorageKey, cancellationToken))
        await using (var output = File.Create(context.SourceVideoPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        context.AudioPath = Path.Combine(context.WorkDirectory, "audio.wav");
        await _media.ExtractAudioAsync(context.SourceVideoPath, context.AudioPath, cancellationToken);

        var key = $"jobs/{context.Job.Id:N}/audio.wav";
        await using var audio = File.OpenRead(context.AudioPath);
        await _storage.SaveAsync(audio, key, "audio/wav", cancellationToken);
        context.Job.Artifacts.Add(new Artifact
        {
            JobId = context.Job.Id,
            Kind = "audio",
            Language = context.Job.SourceLanguage,
            StorageKey = key,
            ContentType = "audio/wav",
            SizeBytes = new FileInfo(context.AudioPath).Length
        });
    }
}
