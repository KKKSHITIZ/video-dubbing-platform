using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Diarization;

public sealed class FfmpegSilenceDiarizationProvider : IDiarizationProvider
{
    private readonly IMediaProcessor _media;

    public FfmpegSilenceDiarizationProvider(IMediaProcessor media) => _media = media;

    public string Name => "FfmpegSilence";

    public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(string audioPath, CancellationToken cancellationToken) =>
        _media.DetectSilenceTurnsAsync(audioPath, cancellationToken);
}

public sealed class PyannoteDiarizationProvider : IDiarizationProvider
{
    public string Name => "Pyannote";

    public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(string audioPath, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Configure Providers:Diarization:Primary to FfmpegSilence or supply a Pyannote endpoint.");
}
