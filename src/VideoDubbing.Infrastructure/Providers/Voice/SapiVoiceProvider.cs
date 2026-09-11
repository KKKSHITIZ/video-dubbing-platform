using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using VideoDubbing.Application.Providers;

#pragma warning disable CA1416 // Windows-only SAPI types are guarded by OperatingSystem.IsWindows() in SynthesizeAsync

namespace VideoDubbing.Infrastructure.Providers.Voice;

/// <summary>
/// Offline, key-free Windows TTS fallback (SAPI). Produces real spoken words even when no
/// cloud voice is configured — no more audible test tones. Picks the best installed voice
/// for the target language (male/female cycled per speaker); when no native voice exists for
/// the language it falls back to a default installed voice so text is always spoken audibly.
/// Writes a 16 kHz mono WAV, the exact shape the pipeline expects.
/// </summary>
public sealed class SapiVoiceProvider : IVoiceProvider
{
    private static readonly string[] FallbackOrder =
    [
        "Microsoft Zira Desktop",   // en-US female
        "Microsoft David Desktop",  // en-US male
        "Microsoft Mark Desktop"    // en-US male
    ];

    public string Name => "Sapi";

    public Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("SapiVoiceProvider requires Windows (System.Speech).");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var synth = new SpeechSynthesizer();
        var voices = synth.GetInstalledVoices()
            .Where(v => v.Enabled && v.VoiceInfo?.Name is not null)
            .Select(v => v.VoiceInfo)
            .ToList();
        if (voices.Count == 0)
        {
            throw new InvalidOperationException("No SAPI voices are installed on this machine.");
        }

        synth.SelectVoice(SelectVoice(voices, language, speakerLabel).Name);
        synth.SetOutputToWaveFile(
            outputPath,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        try
        {
            synth.Volume = 100;
            synth.Rate = 0;
            // SAPI parses text as XML/SSML markup; neutralize angle brackets in raw segments.
            synth.Speak(Sanitize(text));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"SAPI synthesis failed for language '{language}'.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string Sanitize(string text) => text
        .Replace("<", " ")
        .Replace(">", " ")
        .Replace("&", "and")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static VoiceInfo SelectVoice(IReadOnlyList<VoiceInfo> voices, string language, string speakerLabel)
    {
        var targetLang = (language ?? string.Empty).Split('-')[0].ToLowerInvariant();
        var preferFemale = Math.Abs(speakerLabel.GetHashCode()) % 2 == 0;

        // 1) exact language match, respecting gender preference
        var exact =
            voices.FirstOrDefault(v => Matches(v, targetLang) && preferFemale && v.Gender == VoiceGender.Female) ??
            voices.FirstOrDefault(v => Matches(v, targetLang) && !preferFemale && v.Gender == VoiceGender.Male) ??
            voices.FirstOrDefault(v => Matches(v, targetLang));
        if (exact is not null)
        {
            return exact;
        }

        // 2) known fallback list (installed EN voices always pronounce the text audibly)
        var known = FallbackOrder.Select(name => voices.FirstOrDefault(v => v.Name == name)).FirstOrDefault(v => v is not null);
        if (known is not null)
        {
            return known;
        }

        // 3) any installed voice
        return voices[Math.Abs(speakerLabel.GetHashCode()) % voices.Count];
    }

    private static bool Matches(VoiceInfo voice, string targetLang)
    {
        var voiceLang = (voice.Culture?.Name ?? string.Empty).Split('-')[0].ToLowerInvariant();
        return voiceLang == targetLang ||
               (voice.Culture is not null &&
                voice.Culture.TwoLetterISOLanguageName.Equals(targetLang, StringComparison.OrdinalIgnoreCase));
    }
}
#pragma warning restore CA1416