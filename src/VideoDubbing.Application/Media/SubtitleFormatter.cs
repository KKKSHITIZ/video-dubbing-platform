using System.Text;

namespace VideoDubbing.Application.Media;

/// <summary>
/// Builds SubRip (SRT) subtitle documents from timed transcript turns.
/// Extracted as a standalone utility so the formatting logic is unit-testable.
/// </summary>
public static class SubtitleFormatter
{
    public static string BuildSrt(IReadOnlyList<TranscriptTurn> turns)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{Format(turn.StartSeconds)} --> {Format(turn.EndSeconds)}");
            sb.AppendLine($"{turn.SpeakerLabel}: {turn.Text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string BuildVtt(IReadOnlyList<TranscriptTurn> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            sb.AppendLine($"{FormatVtt(turn.StartSeconds)} --> {FormatVtt(turn.EndSeconds)}");
            sb.AppendLine($"{turn.SpeakerLabel}: {turn.Text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Format(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    public static string FormatVtt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
