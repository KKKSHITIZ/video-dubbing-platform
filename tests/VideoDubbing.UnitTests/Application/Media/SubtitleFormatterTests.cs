using FluentAssertions;
using VideoDubbing.Application.Media;

namespace VideoDubbing.UnitTests.Application.Media;

public sealed class SubtitleFormatterTests
{
    [Fact]
    public void BuildSrt_emits_sequential_blocks()
    {
        var turns = new List<TranscriptTurn>
        {
            new("SPEAKER_00", 0.0, 1.5, "Hello world", "en"),
            new("SPEAKER_01", 1.6, 3.2, "Hi there", "en")
        };

        var srt = SubtitleFormatter.BuildSrt(turns);

        srt.Should().Contain("1").And.Contain("2");
        srt.Should().Contain("SPEAKER_00: Hello world");
        srt.Should().Contain("SPEAKER_01: Hi there");
    }

    [Fact]
    public void BuildSrt_handles_empty_input()
    {
        SubtitleFormatter.BuildSrt([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(65.25, "00:01:05,250")]
    [InlineData(3661.5, "01:01:01,500")]
    [InlineData(1.5, "00:00:01,500")]
    public void Format_produces_srt_timestamp(double seconds, string expected)
    {
        SubtitleFormatter.Format(seconds).Should().Be(expected);
    }
}
