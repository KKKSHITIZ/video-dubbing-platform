using FluentAssertions;
using Moq;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;
using VideoDubbing.Infrastructure.Providers.Diarization;
using VideoDubbing.Infrastructure.Providers.Speech;
using VideoDubbing.Infrastructure.Providers.Translation;
using VideoDubbing.Infrastructure.Providers.Voice;

namespace VideoDubbing.UnitTests.Infrastructure.Providers;

public sealed class ProviderBehaviorTests
{
    private static readonly IReadOnlyList<SpeakerTurn> TwoTurns =
    [
        new("SPEAKER_00", 0, 2),
        new("SPEAKER_01", 2, 4)
    ];

    [Fact]
    public async Task MockSpeechToText_uses_source_language_when_specified()
    {
        var provider = new MockSpeechToTextProvider();
        var (language, turns) = await provider.TranscribeAsync("a.wav", TwoTurns, "es", CancellationToken.None);

        language.Should().Be("es");
        turns.Should().HaveCount(2);
        turns[0].SpeakerLabel.Should().Be("SPEAKER_00");
        turns[0].Text.Should().Contain("SPEAKER_00");
    }

    [Fact]
    public async Task MockSpeechToText_defaults_to_english_for_auto()
    {
        var provider = new MockSpeechToTextProvider();
        var (language, _) = await provider.TranscribeAsync("a.wav", TwoTurns, "auto", CancellationToken.None);

        language.Should().Be("en");
    }

    [Fact]
    public async Task MockTranslation_prefixes_target_language()
    {
        var provider = new MockTranslationProvider();
        var request = new TranslationRequest(
            [new TranslationSegment("a", "SPEAKER_00", 0, 1), new TranslationSegment("b", "SPEAKER_00", 1, 2)],
            "en",
            "fr");
        var translated = await provider.TranslateAsync(request, CancellationToken.None);

        translated.Should().HaveCount(2);
        translated[0].Should().Be("[fr] a");
        translated[1].Should().Be("[fr] b");
    }

    [Fact]
    public async Task LocalFfmpegVoiceProvider_generates_a_tone_file()
    {
        var media = new Mock<IMediaProcessor>();
        var provider = new LocalFfmpegVoiceProvider(media.Object);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");

        await provider.SynthesizeAsync("hi", "SPEAKER_01", "en", path, 1.0, CancellationToken.None);

        media.Verify(x => x.GenerateToneAsync(path, It.Is<double>(d => d >= 1.0), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        // SPEAKER_01 base 220 + en offset 91 = 311 (language-aware pitch keeps audio tracks distinct)
        media.Verify(x => x.GenerateToneAsync(path, It.IsAny<double>(), 311, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LocalFfmpegVoiceProvider_uses_distinct_frequencies_for_speakers()
    {
        var media = new Mock<IMediaProcessor>();
        var provider = new LocalFfmpegVoiceProvider(media.Object);
        var p0 = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        var p1 = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");

        await provider.SynthesizeAsync("a", "SPEAKER_00", "en", p0, 1.0, CancellationToken.None);
        await provider.SynthesizeAsync("b", "SPEAKER_01", "en", p1, 1.0, CancellationToken.None);

        media.Verify(x => x.GenerateToneAsync(p0, It.IsAny<double>(), 421, It.IsAny<CancellationToken>()), Times.Once);
        media.Verify(x => x.GenerateToneAsync(p1, It.IsAny<double>(), 311, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FfmpegSilenceDiarization_delegates_to_media_processor()
    {
        var media = new Mock<IMediaProcessor>();
        media.Setup(x => x.DetectSilenceTurnsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoTurns);
        var provider = new FfmpegSilenceDiarizationProvider(media.Object);

        var result = await provider.DiarizeAsync("a.wav", CancellationToken.None);
        result.Should().Equal(TwoTurns);
    }

    [Fact]
    public async Task Unconfigured_cloud_providers_throw_clear_errors()
    {
        ISpeechToTextProvider assembly = new AssemblyAiSpeechToTextProvider();
        Func<Task> act = () => assembly.TranscribeAsync("a.wav", TwoTurns, "auto", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AssemblyAI*");
    }

    [Fact]
    public void Provider_registration_set_has_expected_providers()
    {
        var all = new List<IDiarizationProvider>
        {
            new FfmpegSilenceDiarizationProvider(Mock.Of<IMediaProcessor>()),
            new PyannoteDiarizationProvider()
        };
        all.Select(p => p.Name).Should().Contain("FfmpegSilence").And.Contain("Pyannote");
    }
}
