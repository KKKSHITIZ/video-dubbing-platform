using FluentAssertions;
using Moq;
using VideoDubbing.Application.Abstractions;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Pipeline;
using VideoDubbing.Application.Pipeline.Steps;
using VideoDubbing.Application.Providers;
using VideoDubbing.Application.Storage;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.UnitTests.Application.Pipeline;

public sealed class PipelineStepTests
{
    private static PipelineContext Context(Job job, string workDir) => new() { Job = job, WorkDirectory = workDir };

    [Fact]
    public async Task DiarizeStep_persists_speakers_and_durations()
    {
        var provider = new Mock<IDiarizationProvider>();
        provider.Setup(x => x.Name).Returns("Mock");
        provider.Setup(x => x.DiarizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SpeakerTurn>
            {
                new("SPEAKER_00", 0, 2),
                new("SPEAKER_00", 2, 3),
                new("SPEAKER_01", 3, 6)
            });
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(x => x.DiarizationChain(It.IsAny<double>()))
            .Returns(new List<IDiarizationProvider> { provider.Object });
        var job = new Job { DurationSeconds = 6 };
        var context = Context(job, Path.GetTempPath());
        var step = new DiarizeStep(resolver.Object);

        await step.ExecuteAsync(context, CancellationToken.None);

        job.Speakers.Should().HaveCount(2);
        job.Speakers.Single(s => s.Label == "SPEAKER_00").SpeakingDurationSeconds.Should().Be(3);
        job.Speakers.Single(s => s.Label == "SPEAKER_01").SpeakingDurationSeconds.Should().Be(3);
    }

    [Fact]
    public async Task TranslateStep_populates_segment_translations()
    {
        var provider = new Mock<ITranslationProvider>();
        provider.Setup(x => x.Name).Returns("Mock");
        provider.Setup(x => x.TranslateAsync(It.IsAny<TranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "[es] hello" });
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(x => x.TranslationChain(It.IsAny<double>()))
            .Returns(new List<ITranslationProvider> { provider.Object });
        var job = new Job
        {
            DetectedLanguage = "en",
            TargetLanguages = ["es"],
            DurationSeconds = 5
        };
        var segment = new TranscriptSegment
        {
            SourceText = "hello"
        };
        job.Segments.Add(segment);
        var context = Context(job, Path.GetTempPath());
        context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1, "hello", "en"));
        var step = new TranslateStep(resolver.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoDubbing.Application.Pipeline.Steps.TranslateStep>());

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Translations.Should().ContainKey("es");
        context.Translations["es"][0].Text.Should().Be("[es] hello");
        segment.TranslationsJson.Should().Contain("\"es\"");
    }

    [Fact]
    public async Task TranslateStep_skips_caption_only_modes()
    {
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(x => x.TranslationChain(It.IsAny<double>()))
            .Returns(new List<ITranslationProvider> { Mock.Of<ITranslationProvider>(p => p.Name == "Mock") });
        var job = new Job { Mode = JobMode.Subtitles.ToString(), DetectedLanguage = "en", TargetLanguages = ["en"] };
        var context = Context(job, Path.GetTempPath());
        context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1, "hello", "en"));
        var step = new TranslateStep(resolver.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoDubbing.Application.Pipeline.Steps.TranslateStep>());

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Translations.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeStep_skips_caption_only_modes()
    {
        var resolver = new Mock<IProviderResolver>();
        var job = new Job { Mode = JobMode.Transcript.ToString(), TargetLanguages = ["en"], DurationSeconds = 2 };
        var context = Context(job, Path.GetTempPath());
        var step = new SynthesizeStep(resolver.Object, Mock.Of<IVoiceProfileRegistry>(), Mock.Of<IMediaProcessor>(), Mock.Of<IJobProgressPublisher>());

        await step.ExecuteAsync(context, CancellationToken.None);

        context.ClipsByLanguage.Should().BeEmpty();
    }

    [Fact]
    public async Task SynchronizeAndExport_voiceover_mixes_over_original()
    {
        var media = new Mock<IMediaProcessor>();
        media.Setup(x => x.BuildAudioTrackAsync(It.IsAny<IReadOnlyList<SynthesizedClip>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("track.wav");
        media.Setup(x => x.MixVoiceOverAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        media.Setup(x => x.MuxAudioWithVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, outputPath, _) => File.WriteAllText(outputPath, "mp4"))
            .Returns(Task.CompletedTask);
        var storage = new Mock<IObjectStorage>();
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}")).FullName;
        try
        {
            var job = new Job { Mode = JobMode.VoiceOver.ToString(), Subtitles = SubtitleStyle.Translated.ToString(), TargetLanguages = ["es"], DetectedLanguage = "en", DurationSeconds = 3 };
            var context = Context(job, workDir);
            context.AudioPath = "original.wav";
            context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1, "hello", "en"));
            context.Translations["es"] = new List<TranscriptTurn> { new("SPEAKER_00", 0, 1, "hola", "es") };
            context.ClipsByLanguage["es"] = new List<SynthesizedClip> { new("SPEAKER_00", 0, "clip.wav", 1) };
            var step = new SynchronizeAndExportStep(media.Object, storage.Object, Mock.Of<IProviderResolver>(), Mock.Of<IJobProgressPublisher>());

            await step.ExecuteAsync(context, CancellationToken.None);

            media.Verify(x => x.MixVoiceOverAsync("original.wav", "track.wav", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            job.Artifacts.Should().Contain(a => a.Kind == "video");
            job.Artifacts.Should().Contain(a => a.Kind == "subtitles");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAndExport_subtitles_keeps_original_audio()
    {
        var media = new Mock<IMediaProcessor>();
        media.Setup(x => x.MuxAudioWithVideoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, outputPath, _) => File.WriteAllText(outputPath, "mp4"))
            .Returns(Task.CompletedTask);
        var storage = new Mock<IObjectStorage>();
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}")).FullName;
        try
        {
            var job = new Job { Mode = JobMode.Subtitles.ToString(), TargetLanguages = ["en"], DetectedLanguage = "en" };
            var context = Context(job, workDir);
            context.AudioPath = "original.wav";
            context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1, "hello", "en"));
            var step = new SynchronizeAndExportStep(media.Object, storage.Object, Mock.Of<IProviderResolver>(), Mock.Of<IJobProgressPublisher>());

            await step.ExecuteAsync(context, CancellationToken.None);

            media.Verify(x => x.MuxAudioWithVideoAsync(It.IsAny<string>(), "original.wav", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            media.Verify(x => x.BuildAudioTrackAsync(It.IsAny<IReadOnlyList<SynthesizedClip>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            job.Artifacts.Should().Contain(a => a.Kind == "subtitles" && a.Language == "en");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAndExport_transcript_mode_never_writes_a_video()
    {
        var storage = new Mock<IObjectStorage>();
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}")).FullName;
        try
        {
            var job = new Job { Mode = JobMode.Transcript.ToString(), TargetLanguages = ["en"], DetectedLanguage = "en" };
            var context = Context(job, workDir);
            context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1, "hello", "en"));
            var step = new SynchronizeAndExportStep(Mock.Of<IMediaProcessor>(), storage.Object, Mock.Of<IProviderResolver>(), Mock.Of<IJobProgressPublisher>());

            await step.ExecuteAsync(context, CancellationToken.None);

            job.Artifacts.Should().NotContain(a => a.Kind == "video");
            job.Artifacts.Should().Contain(a => a.Kind == "subtitles" && a.Language == "en");
            job.Artifacts.Should().Contain(a => a.Kind == "transcript");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task SynthesizeStep_produces_clips_for_each_language()
    {
        var voice = new Mock<IVoiceProvider>();
        voice.Setup(x => x.Name).Returns("Local");
        voice.Setup(x => x.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Callback<string, string, string, string, double, CancellationToken, string>((_, _, _, outputPath, _, _, _) =>
            {
                var dir = Path.GetDirectoryName(outputPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(outputPath, "fake-wav");
            })
            .Returns(Task.CompletedTask);
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(x => x.VoiceChain(It.IsAny<double>()))
            .Returns(new List<IVoiceProvider> { voice.Object });
        var media = new Mock<IMediaProcessor>();
        media.Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.5, "wav", null, null, true));
        media.Setup(x => x.TimeStretchAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string src, double end, double start, string output, CancellationToken _) =>
            {
                File.Copy(src, output, overwrite: true);
                return output;
            });
        var profiles = Mock.Of<IVoiceProfileRegistry>(p =>
            p.ResolveVoiceId(It.IsAny<Guid>(), It.IsAny<string>()) == "voice-0001");
        var workDir = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var job = new Job { TargetLanguages = ["es"], DurationSeconds = 5 };
            job.Speakers.Add(new Speaker { Label = "SPEAKER_00" });
            var context = Context(job, workDir);
            context.Transcript.Add(new TranscriptTurn("SPEAKER_00", 0, 1.5, "hello", "en"));
            context.Translations["es"] = new List<TranscriptTurn>
            {
                new("SPEAKER_00", 0, 1.5, "hola", "es")
            };
            var step = new SynthesizeStep(resolver.Object, profiles, media.Object, Mock.Of<IJobProgressPublisher>());

            await step.ExecuteAsync(context, CancellationToken.None);

            context.ClipsByLanguage.Should().ContainKey("es");
            context.ClipsByLanguage["es"].Should().HaveCount(1);
            File.Exists(context.ClipsByLanguage["es"][0].AudioPath).Should().BeTrue();
            job.Speakers.Single().VoiceId.Should().Be("voice-0001");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
