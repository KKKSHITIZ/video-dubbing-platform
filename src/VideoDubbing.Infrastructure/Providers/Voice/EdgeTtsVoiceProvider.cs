using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Voice;

/// <summary>
/// Key-free TTS via Microsoft Edge's public online voices (the <c>edge-tts</c> CLI).
/// No account or API key required; produces genuinely native multilingual speech.
/// Used as the last voice-chain fallback so Dub mode works out of the box.
/// </summary>
public sealed class EdgeTtsVoiceProvider : IVoiceProvider
{
    private static readonly Dictionary<string, string> Voices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en-US-AvaMultilingualNeural",
        ["en-US"] = "en-US-AvaNeural",
        ["en-GB"] = "en-GB-SoniaNeural",
        ["es"] = "es-ES-ElviraNeural",
        ["es-ES"] = "es-ES-ElviraNeural",
        ["fr"] = "fr-FR-DeniseNeural",
        ["fr-FR"] = "fr-FR-DeniseNeural",
        ["de"] = "de-DE-KatjaNeural",
        ["it"] = "it-IT-ElsaNeural",
        ["pt"] = "pt-PT-RaquelNeural",
        ["pt-BR"] = "pt-BR-FranciscaNeural",
        ["nl"] = "nl-NL-ColetteNeural",
        ["ja"] = "ja-JP-NanamiNeural",
        ["zh"] = "zh-CN-XiaoxiaoNeural",
        ["ko"] = "ko-KR-SunHiNeural",
        ["hi"] = "hi-IN-SwaraNeural",
        ["ru"] = "ru-RU-SvetlanaNeural",
        ["tr"] = "tr-TR-EmelNeural",
        ["pl"] = "pl-PL-ZofiaNeural",
        ["ar"] = "ar-SA-ZariyahNeural"
    };

    private static readonly string DefaultVoice = "en-US-AvaMultilingualNeural";

    private readonly string _cliPath;

    public EdgeTtsVoiceProvider(IOptions<ProviderOptions> options) =>
        _cliPath = string.IsNullOrWhiteSpace(options.Value.EdgeTtsCliPath) ? "edge-tts" : options.Value.EdgeTtsCliPath;

    public string Name => "EdgeTts";

    public async Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Empty text segment.");
        }

        Voices.TryGetValue(Normalize(language), out var voice);
        voice ??= DefaultVoice;

        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            Arguments = $"--text \"{Escape(text)}\" --voice {voice} --write-media \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start edge-tts at {_cliPath}. Install with `pip install edge-tts`.");
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"edge-tts failed with exit code {process.ExitCode}.");
        }
    }

    private static string Normalize(string language)
    {
        var region = language.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return region.Length switch
        {
            >= 2 => $"{region[0]}-{region[1]}",
            _ => language
        };
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}