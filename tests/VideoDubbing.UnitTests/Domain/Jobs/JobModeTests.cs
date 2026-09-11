using FluentAssertions;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.UnitTests.Domain.Jobs;

public sealed class JobModeTests
{
    [Theory]
    [InlineData("Dub", JobMode.Dub)]
    [InlineData("VoiceOver", JobMode.VoiceOver)]
    [InlineData("Subtitles", JobMode.Subtitles)]
    [InlineData("Transcript", JobMode.Transcript)]
    [InlineData("Remix", JobMode.Remix)]
    [InlineData("remix", JobMode.Remix)]
    [InlineData("", JobMode.Dub)]
    [InlineData(null, JobMode.Dub)]
    public void TryParse_parses_known_modes_case_insensitively(string? value, JobMode expected)
    {
        JobModes.TryParse(value, out var mode, out var error).Should().BeTrue();
        mode.Should().Be(expected);
        error.Should().BeNull();
    }

    [Fact]
    public void TryParse_rejects_unknown_modes()
    {
        JobModes.TryParse("Teledubbing", out var mode, out var error).Should().BeFalse();
        mode.Should().Be(JobMode.Dub);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(JobMode.Subtitles, true)]
    [InlineData(JobMode.Transcript, true)]
    [InlineData(JobMode.Dub, false)]
    [InlineData(JobMode.VoiceOver, false)]
    [InlineData(JobMode.Remix, false)]
    public void IsCaptionOnly_matches_caption_modes(JobMode mode, bool expected)
        => mode.IsCaptionOnly().Should().Be(expected);

    [Fact]
    public void Job_surfaced_modes_fall_back_to_safe_defaults()
    {
        var job = new Job();
        job.ModeKind.Should().Be(JobMode.Dub);
        job.SubtitleStyleKind.Should().Be(SubtitleStyle.Translated);

        var remix = new Job { Mode = "Remix", Subtitles = "None", Theme = "as pirates" };
        remix.ModeKind.Should().Be(JobMode.Remix);
        remix.SubtitleStyleKind.Should().Be(SubtitleStyle.None);
    }

    [Fact]
    public void SubtitleStyles_parse()
    {
        SubtitleStyles.TryParse("None", out var none).Should().BeTrue();
        none.Should().Be(SubtitleStyle.None);
        SubtitleStyles.TryParse("original", out var original).Should().BeTrue();
        original.Should().Be(SubtitleStyle.Original);
        SubtitleStyles.TryParse("", out var empty).Should().BeTrue();
        empty.Should().Be(SubtitleStyle.Translated);
        SubtitleStyles.TryParse("garbage", out var fallback).Should().BeTrue();
        fallback.Should().Be(SubtitleStyle.Translated);
    }
}