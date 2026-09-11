using FluentAssertions;
using VideoDubbing.Application.Media;

namespace VideoDubbing.UnitTests.Application.Media;

public sealed class TranscriptNormalizerTests
{
    [Fact]
    public void CollapseRepeated_folds_long_identical_runs_into_one()
    {
        var turns = new List<TranscriptTurn>();
        for (var i = 0; i < 12; i++)
        {
            turns.Add(new TranscriptTurn("SPEAKER_00", i, i + 1d, "[MUSIC - HELLO, \"I'm a master\"]", "en"));
        }

        var result = TranscriptNormalizer.CollapseRepeated(turns);

        result.Should().HaveCount(1);
        result[0].Text.Should().Be(turns[0].Text);
        result[0].StartSeconds.Should().Be(0);
        result[0].EndSeconds.Should().Be(12);
    }

    [Fact]
    public void CollapseRepeated_keeps_different_speakers_separate()
    {
        var turns = new List<TranscriptTurn>
        {
            new("SPEAKER_00", 0, 1, "music", "en"),
            new("SPEAKER_01", 1, 2, "music", "en"),
            new("SPEAKER_00", 2, 3, "music", "en")
        };

        var result = TranscriptNormalizer.CollapseRepeated(turns);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void CollapseRepeated_collapses_phrase_repeated_inline_in_one_segment()
    {
        var music = "[MUSIC - HELLO, \"I'm a master\"]";
        var repeats = string.Concat(Enumerable.Repeat(music + " ", 80));
        var turns = new List<TranscriptTurn>
        {
            new("SPEAKER_00", 0, 1, "A lot of people ask me.", "en"),
            new("SPEAKER_00", 1, 280d, "A lot of people ask me. " + repeats, "en"),
            new("SPEAKER_00", 280d, 301d, music, "en")
        };

        var result = TranscriptNormalizer.CollapseRepeated(turns);

        result.Should().HaveCount(3);
        result[1].Text.Should().Contain("A lot of people ask me.");
        result[1].Text.Should().Contain(music);
        result[1].Text.Should().NotContain(music + " " + music);
    }

    [Fact]
    public void CollapsePhraseRepeats_collapses_inline_repeats_but_keeps_single()
    {
        var music = "[MUSIC - HELLO, \"I'm a master\"]";
        var input = string.Concat(Enumerable.Repeat(music + " ", 80)) + "end";
        var result = TranscriptNormalizer.CollapsePhraseRepeats(input);
        result.Should().Be(music + " end");
    }

    [Fact]
    public void CollapsePhraseRepeats_keeps_two_occurrences()
    {
        var line = "A lot of people ask me.";
        var result = TranscriptNormalizer.CollapsePhraseRepeats(line + " " + line);
        result.Should().Be(line + " " + line);
    }

    [Fact]
    public void CollapseRepeated_preserves_short_real_repetitions()
    {
        var turns = new List<TranscriptTurn>
        {
            new("SPEAKER_00", 0, 1, "A lot of people ask me.", "en"),
            new("SPEAKER_00", 1, 2, "A lot of people ask me.", "en")
        };

        var result = TranscriptNormalizer.CollapseRepeated(turns);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void CollapseRepeated_is_case_and_whitespace_insensitive()
    {
        var turns = new List<TranscriptTurn>
        {
            new("SPEAKER_00", 0, 1, "I am a master", "en"),
            new("SPEAKER_00", 1, 2, " i am   a master ", "en"),
            new("SPEAKER_00", 2, 3, "I AM A MASTER", "en")
        };

        var result = TranscriptNormalizer.CollapseRepeated(turns);

        result.Should().HaveCount(1);
        result[0].StartSeconds.Should().Be(0);
        result[0].EndSeconds.Should().Be(3);
    }
}