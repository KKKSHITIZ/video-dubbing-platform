namespace VideoDubbing.Domain.Jobs;

/// <summary>
/// Which kind of output a job produces. Modeled on Dub Studio's switchable modes:
/// <list type="bullet">
/// <item><see cref="Dub"/> — full re-voice into the target language (default).</item>
/// <item><see cref="VoiceOver"/> — translated voice over the ducked original (source stays audible).</item>
/// <item><see cref="Subtitles"/> — original-language captions, original audio, no translation/TTS.</item>
/// <item><see cref="Transcript"/> — clean diarized transcript + caption exports, no audio/video work.</item>
/// <item><see cref="Remix"/> — funny remix: the script is rewritten with a theme, then dubbed.</item>
/// </list>
/// </summary>
public enum JobMode
{
    Dub,
    VoiceOver,
    Subtitles,
    Transcript,
    Remix
}

public static class JobModes
{
    /// <summary>Case-insensitive parse. An empty value means the default (<see cref="JobMode.Dub"/>).</summary>
    public static bool TryParse(string? value, out JobMode mode, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = JobMode.Dub;
            error = null;
            return true;
        }

        if (Enum.TryParse<JobMode>(value, ignoreCase: true, out var parsed))
        {
            mode = parsed;
            error = null;
            return true;
        }

        mode = JobMode.Dub;
        error = $"Unknown mode '{value}'. Allowed: Dub, VoiceOver, Subtitles, Transcript, Remix.";
        return false;
    }

    /// <summary>Modes that produce captions for the *source* language and never synthesize voices.<br/>
    /// Pipeline stages for translation/TTS are skipped; the job's target language becomes the detected one.</summary>
    public static bool IsCaptionOnly(this JobMode mode) => mode is JobMode.Subtitles or JobMode.Transcript;
}

/// <summary>Controls which subtitle files a job produces.</summary>
public enum SubtitleStyle
{
    None,
    Translated,
    Original
}

public static class SubtitleStyles
{
    public static bool TryParse(string? value, out SubtitleStyle style)
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<SubtitleStyle>(value, ignoreCase: true, out var parsed))
        {
            // Unknown / empty values degrade to the common default (translated captions).
            style = SubtitleStyle.Translated;
            return true;
        }

        style = parsed;
        return true;
    }
}