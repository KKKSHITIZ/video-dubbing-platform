using System.Text;
using System.Text.RegularExpressions;

namespace VideoDubbing.Application.Media;

/// <summary>
/// Post-processing that removes Whisper-style hallucination artifacts from a transcript:
/// long runs of identical consecutive segments (typically music or applause transcribed as
/// the same phrase dozens of times) are folded back into a single utterance. Occurrences
/// from different speakers are never merged.
/// </summary>
public static class TranscriptNormalizer
{
    private const int MinRepeatRun = 3;
    private const int MinPhraseLength = 6;

    /// <summary>Cleanup for Whisper-style hallucination artifacts. Two layers:
    /// <list type="bullet">
    /// <item><b>Text level</b> — inside a single segment, a phrase repeated 3+ times inline
    /// (e.g. <c>[MUSIC - HELLO, "I'm a master"]</c> dozens of times) collapses to one occurrence.</item>
    /// <item><b>Turn level</b> — long runs of identical consecutive segments collapse into one,
    /// keeping the first start and the last end time. Occurrences from different speakers are never merged.</item>
    /// </list></summary>
    public static IReadOnlyList<TranscriptTurn> CollapseRepeated(IReadOnlyList<TranscriptTurn> turns)
    {
        if (turns.Count == 0)
        {
            return turns;
        }

        var cleaned = turns
            .Select(t => new TranscriptTurn(t.SpeakerLabel, t.StartSeconds, t.EndSeconds, CollapsePhraseRepeats(t.Text), t.Language))
            .ToList();

        if (cleaned.Count < MinRepeatRun)
        {
            return cleaned;
        }

        var results = new List<TranscriptTurn>(cleaned.Count);
        var run = new List<TranscriptTurn>(MinRepeatRun);
        string? runKey = null;

        foreach (var turn in cleaned)
        {
            var key = Normalize(turn.Text);
            if (run.Count > 0 && (key != runKey || !run[0].SpeakerLabel.Equals(turn.SpeakerLabel, StringComparison.Ordinal)))
            {
                Flush(results, run, runKey);
                run.Clear();
                runKey = null;
            }

            if (run.Count == 0)
            {
                runKey = key;
            }

            run.Add(turn);
        }

        Flush(results, run, runKey);
        return results;
    }

    /// <summary>Collapses a phrase repeated 3+ times consecutively within one transcript string
    /// down to a single occurrence. Uses an exact consecutive-run scan (longest phrase first).</summary>
    public static string CollapsePhraseRepeats(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var current = Regex.Replace(text, @"\s+", " ").Trim();
        while (current.Length >= MinPhraseLength * MinRepeatRun)
        {
            current = Regex.Replace(current, @"\s+", " ").Trim();
            var (index, length, count) = FindLongestRunInternal(current);
            if (length < MinPhraseLength || count < MinRepeatRun)
            {
                break;
            }

            var phrase = current.Substring(index, length);
            if (phrase.Trim().Length < MinPhraseLength)
            {
                break;
            }

            current = current.Remove(index, length * count).Insert(index, phrase);
        }

        return current;
    }

    /// <summary>Finds the consecutive repeated substring that removes the most characters
    /// (longest run of the longest unit). Returns start index, unit length and repeat count,
    /// or (0,0,0) when no run of >= <see cref="MinRepeatRun"/> exists.</summary>
    public static (int Index, int Length, int Count) FindLongestRunInternal(string text)
    {
        var bestIndex = 0;
        var bestLen = 0;
        var bestCount = 0;
        var bestArea = 0;

        var maxLen = Math.Min(text.Length / MinRepeatRun, 200);
        for (var len = maxLen; len >= MinPhraseLength; len--)
        {
            var limit = text.Length - len;
            for (var i = 0; i <= limit; i++)
            {
                var phrase = text.Substring(i, len);
                var count = 1;
                for (var j = i + len; j + len <= text.Length; j += len)
                {
                    if (!text.Substring(j, len).Equals(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    count++;
                }

                if (count >= MinRepeatRun)
                {
                    var area = count * len;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIndex = i;
                        bestLen = len;
                        bestCount = count;
                    }
                }
            }
        }

        return bestCount > 0 ? (bestIndex, bestLen, bestCount) : (0, 0, 0);
    }

    private static void Flush(List<TranscriptTurn> results, List<TranscriptTurn> run, string? runKey)
    {
        if (run.Count >= MinRepeatRun && !string.IsNullOrWhiteSpace(runKey))
        {
            var first = run[0];
            var last = run[^1];
            results.Add(new TranscriptTurn(
                first.SpeakerLabel,
                Math.Round(first.StartSeconds, 3),
                Math.Round(Math.Max(last.EndSeconds, first.StartSeconds + 0.1), 3),
                first.Text,
                first.Language));
            return;
        }

        results.AddRange(run);
    }

    private static string Normalize(string? text)
    {
        var value = text ?? string.Empty;
        return Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    }
}