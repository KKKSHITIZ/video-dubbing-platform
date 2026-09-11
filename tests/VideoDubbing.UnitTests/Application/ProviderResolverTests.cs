using FluentAssertions;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Infrastructure.Providers;

namespace VideoDubbing.UnitTests.Application;

public sealed class ProviderResolverTests
{
    private sealed class FakeDiarization : IDiarizationProvider
    {
        public FakeDiarization(string name) => Name = name;
        public string Name { get; }
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(string audioPath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>([]);
    }

    private sealed class FakeStt : ISpeechToTextProvider
    {
        public FakeStt(string name) => Name = name;
        public string Name { get; }
        public Task<(string, IReadOnlyList<TranscriptTurn>)> TranscribeAsync(string audioPath, IReadOnlyList<SpeakerTurn> turns, string sourceLanguage, CancellationToken ct) =>
            Task.FromResult<(string, IReadOnlyList<TranscriptTurn>)>(("en", new List<TranscriptTurn>()));
    }

    private sealed class FakeTranslation : ITranslationProvider
    {
        public FakeTranslation(string name) => Name = name;
        public string Name { get; }
        public Task<IReadOnlyList<string>> TranslateAsync(TranslationRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(new List<string>());
    }

    private sealed class FakeVoice : IVoiceProvider
    {
        public FakeVoice(string name) => Name = name;
        public string Name { get; }
        public Task SynthesizeAsync(string text, string speakerLabel, string language, string outputPath, double targetDurationSeconds, CancellationToken ct, string? referenceAudioPath = null) =>
            Task.CompletedTask;
    }

    private sealed class FakeLipSync : ILipSyncEngine
    {
        public FakeLipSync(string name) => Name = name;
        public string Name { get; }
        public Task<string> LipSyncAsync(string originalVideoPath, string targetAudioPath, string outputVideoPath, string voiceLabel, CancellationToken ct) =>
            Task.FromResult(originalVideoPath);
    }

    private ProviderResolver Create(ProviderOptions options)
    {
        var providers = new object[]
        {
            new FakeDiarization("FfmpegSilence"),
            new FakeDiarization("Pyannote"),
            new FakeStt("Mock"),
            new FakeStt("OpenAiWhisper"),
            new FakeStt("Deepgram"),
            new FakeTranslation("Mock"),
            new FakeTranslation("OpenAi"),
            new FakeTranslation("DeepL"),
            new FakeVoice("LocalFfmpeg"),
            new FakeVoice("ElevenLabs"),
            new FakeLipSync("Passthrough")
        };
        return new ProviderResolver(
            Options.Create(options),
            providers.OfType<IDiarizationProvider>(),
            providers.OfType<ISpeechToTextProvider>(),
            providers.OfType<ITranslationProvider>(),
            providers.OfType<IVoiceProvider>(),
            providers.OfType<ILipSyncEngine>());
    }

    [Fact]
    public void ResolveSpeechToText_returns_configured_primary()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = false,
            EnableFallback = false,
            SpeechToText = new ProviderChain { Primary = "OpenAiWhisper" }
        });

        resolver.ResolveSpeechToText(0).Name.Should().Be("OpenAiWhisper");
    }

    [Fact]
    public void SpeechToTextChain_orders_primary_then_fallbacks()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = false,
            EnableFallback = true,
            SpeechToText = new ProviderChain { Primary = "OpenAiWhisper", Fallbacks = ["Deepgram"] }
        });

        resolver.SpeechToTextChain(0).Select(p => p.Name).Should().Equal(["OpenAiWhisper", "Deepgram"]);
    }

    [Fact]
    public void Short_job_uses_cheap_provider_before_primary_when_cost_routing_enabled()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = true,
            EnableFallback = true,
            CostAwareShortJobSeconds = 120,
            SpeechToText = new ProviderChain { Primary = "OpenAiWhisper", Fallbacks = ["Deepgram"] }
        });

        // short job (< 120s) => cheap "Mock" first, then primary, then fallbacks
        resolver.SpeechToTextChain(60).Select(p => p.Name).Should().Equal(["Mock", "OpenAiWhisper", "Deepgram"]);
    }

    [Fact]
    public void Long_job_does_not_inject_cheap_provider()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = true,
            EnableFallback = true,
            CostAwareShortJobSeconds = 120,
            SpeechToText = new ProviderChain { Primary = "OpenAiWhisper" }
        });

        resolver.SpeechToTextChain(300).Select(p => p.Name).Should().Equal(["OpenAiWhisper"]);
    }

    [Fact]
    public void Disabled_fallback_returns_only_primary()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = false,
            EnableFallback = false,
            Translation = new ProviderChain { Primary = "OpenAi", Fallbacks = ["DeepL"] }
        });

        resolver.TranslationChain(0).Select(p => p.Name).Should().Equal(["OpenAi"]);
    }

    [Fact]
    public void Unknown_provider_throws()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = false,
            Voice = new ProviderChain { Primary = "NotARealProvider" }
        });

        var act = () => resolver.ResolveVoice(0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Unknown provider*");
    }

    [Fact]
    public void Duplicate_names_are_deduplicated()
    {
        var resolver = Create(new ProviderOptions
        {
            EnableCostAwareRouting = false,
            EnableFallback = true,
            Voice = new ProviderChain { Primary = "LocalFfmpeg", Fallbacks = ["LocalFfmpeg"] }
        });

        resolver.VoiceChain(0).Select(p => p.Name).Should().Equal(["LocalFfmpeg"]);
    }
}
