using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers;

public sealed class ProviderResolver : IProviderResolver
{
    private readonly ProviderOptions _options;
    private readonly IReadOnlyDictionary<string, IDiarizationProvider> _diarization;
    private readonly IReadOnlyDictionary<string, ISpeechToTextProvider> _stt;
    private readonly IReadOnlyDictionary<string, ITranslationProvider> _translation;
    private readonly IReadOnlyDictionary<string, IVoiceProvider> _voice;
    private readonly IReadOnlyDictionary<string, ILipSyncEngine> _lipSync;

    public ProviderResolver(
        IOptions<ProviderOptions> options,
        IEnumerable<IDiarizationProvider> diarization,
        IEnumerable<ISpeechToTextProvider> stt,
        IEnumerable<ITranslationProvider> translation,
        IEnumerable<IVoiceProvider> voice,
        IEnumerable<ILipSyncEngine> lipSync)
    {
        _options = options.Value;
        _diarization = diarization.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _stt = stt.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _translation = translation.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _voice = voice.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _lipSync = lipSync.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IDiarizationProvider ResolveDiarization(double durationSeconds) => DiarizationChain(durationSeconds)[0];
    public ISpeechToTextProvider ResolveSpeechToText(double durationSeconds) => SpeechToTextChain(durationSeconds)[0];
    public ITranslationProvider ResolveTranslation(double durationSeconds) => TranslationChain(durationSeconds)[0];
    public IVoiceProvider ResolveVoice(double durationSeconds) => VoiceChain(durationSeconds)[0];

    public ILipSyncEngine ResolveLipSync() =>
        _lipSync.TryGetValue(_options.LipSync, out var engine) ? engine : _lipSync["Passthrough"];

    public IReadOnlyList<IDiarizationProvider> DiarizationChain(double durationSeconds) =>
        Resolve(_options.Diarization, _diarization, durationSeconds, cheap: "FfmpegSilence");

    public IReadOnlyList<ISpeechToTextProvider> SpeechToTextChain(double durationSeconds) =>
        Resolve(_options.SpeechToText, _stt, durationSeconds, cheap: "Mock");

    public IReadOnlyList<ITranslationProvider> TranslationChain(double durationSeconds) =>
        Resolve(_options.Translation, _translation, durationSeconds, cheap: "Mock");

    public IReadOnlyList<IVoiceProvider> VoiceChain(double durationSeconds) =>
        Resolve(_options.Voice, _voice, durationSeconds, cheap: "LocalFfmpeg");

    private IReadOnlyList<T> Resolve<T>(ProviderChain chain, IReadOnlyDictionary<string, T> map, double durationSeconds, string cheap)
    {
        var names = new List<string>();
        if (_options.EnableCostAwareRouting && durationSeconds > 0 && durationSeconds <= _options.CostAwareShortJobSeconds)
        {
            names.Add(cheap);
        }

        names.Add(chain.Primary);
        if (_options.EnableFallback)
        {
            names.AddRange(chain.Fallbacks);
        }

        var providers = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => map.TryGetValue(name, out var provider) ? provider : throw new InvalidOperationException($"Unknown provider '{name}'."))
            .ToList();
        return providers;
    }
}
