using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VideoDubbing.Application.Configuration;
using VideoDubbing.Application.Media;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Voice;

public sealed class LocalFfmpegVoiceProvider : IVoiceProvider
{
    private readonly IMediaProcessor _media;

    public LocalFfmpegVoiceProvider(IMediaProcessor media) => _media = media;

    public string Name => "LocalFfmpeg";

    public Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        // Per-speaker base pitch keeps Speaker 1 audibly distinct from Speaker 2.
        var baseFrequency = speakerLabel.Contains("01", StringComparison.Ordinal) ? 220 : 330;
        // Per-language offset makes each target-language audio track audibly distinct so
        // switching audio tracks (YouTube-style) is clearly perceivable in demo mode.
        var frequency = baseFrequency + LanguagePitchOffset(language);
        return _media.GenerateToneAsync(outputPath, Math.Max(0.2, targetDurationSeconds), frequency, cancellationToken);
    }

    private static int LanguagePitchOffset(string language)
    {
        var hash = 0;
        foreach (var c in language)
        {
            hash = (hash * 31 + c) % 251;
        }

        return 42 + (hash % 90);
    }
}

/// <summary>
/// Bridges to an open source TTS model (XTTS-v2 or Bark) exposed via a CLI or a thin
/// Docker container (e.g. <c>docker run ... python xtts_cli.py</c>). Provide the executable
/// through <c>Providers:VoiceCliPath</c>. When a reference audio clip is supplied it is used
/// for voice cloning so the generated voice matches the original speaker's timbre.
/// </summary>
public sealed class OpenSourceCliVoiceProvider : IVoiceProvider
{
    private readonly string _voiceCliPath;
    private readonly string _providerName;

    public OpenSourceCliVoiceProvider(string voiceCliPath, string providerName = "XTTS-Bark")
    {
        _voiceCliPath = voiceCliPath;
        _providerName = providerName;
    }

    public string Name => _providerName;

    public async Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        if (string.IsNullOrWhiteSpace(_voiceCliPath))
        {
            throw new InvalidOperationException("Open-source voice synthesis is not configured. Set Providers:VoiceCliPath to your XTTS/Bark CLI (see README).");
        }

        var args = new List<string>
        {
            "--text", Quote(text),
            "--out", Quote(outputPath),
            "--language", language,
            "--speaker", speakerLabel,
            "--duration", targetDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(referenceAudioPath))
        {
            args.Add("--reference");
            args.Add(Quote(referenceAudioPath));
        }

        var psi = new ProcessStartInfo
        {
            FileName = _voiceCliPath,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start voice CLI at {_voiceCliPath}.");
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"Voice CLI failed with exit code {process.ExitCode}.");
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

/// <summary>
/// Real ElevenLabs multilingual text-to-speech client. Produces a 16 kHz mono WAV per
/// segment (same shape the pipeline expects) via the <c>pcm_16000</c> output format, using
/// <c>eleven_multilingual_v2</c> so French keeps a natural, consistent voice per speaker.
/// Voices are assigned deterministically per speaker; override with
/// <c>Providers:ElevenLabsVoiceIds</c> (ordered list of voice IDs).
/// </summary>
public sealed class ElevenLabsVoiceProvider : IVoiceProvider
{
    private const int StreamBufferBytes = 1 << 20;
    private const int MaxAttempts = 3;

    private static readonly string[] DefaultVoices =
    [
        "21m00Tcm4TlvDq8ikWAM", // Rachel
        "AZnzlk1XvdvUeBnXmlld", // Domi
        "EXAVITQu4vr4xnSDxMaL", // Bella
        "ErXwobaYiN019PkySvjV", // Antoni
        "TX3LPaxmHKxFdv7VOQHJ"  // Adam
    ];

    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;
    private readonly string[] _voiceIds;
    private readonly ConcurrentDictionary<string, string> _clonedVoices = new(StringComparer.OrdinalIgnoreCase);

    public ElevenLabsVoiceProvider(HttpClient http, AiSecretsOptions secrets, IOptions<ProviderOptions> providers)
    {
        _http = http;
        _secrets = secrets;
        _voiceIds = providers.Value.ElevenLabsVoiceIds is { Length: > 0 } ? providers.Value.ElevenLabsVoiceIds : DefaultVoices;
    }

    public string Name => "ElevenLabs";

    public async Task SynthesizeAsync(
        string text,
        string speakerLabel,
        string language,
        string outputPath,
        double targetDurationSeconds,
        CancellationToken cancellationToken,
        string? referenceAudioPath = null)
    {
        if (string.IsNullOrWhiteSpace(_secrets.ElevenLabsApiKey) ||
            _secrets.ElevenLabsApiKey.Trim().Equals("YOUR_ELEVENLABS_KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ElevenLabs is not configured. Set AiSecrets:ElevenLabsApiKey (https://elevenlabs.io).");
        }

        var voiceId = !string.IsNullOrWhiteSpace(referenceAudioPath)
            ? await ResolveClonedVoiceAsync(speakerLabel, referenceAudioPath, cancellationToken)
            : ResolveVoice(speakerLabel);
        var payload = JsonSerializer.Serialize(new
        {
            text,
            model_id = "eleven_multilingual_v2",
            output_format = "pcm_16000",
            voice_settings = new { stability = 0.45, similarity_boost = 0.8 }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_secrets.ElevenLabsBaseUrl.TrimEnd('/')}/v1/text-to-speech/{voiceId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("xi-api-key", _secrets.ElevenLabsApiKey);

        using var response = await SendSynthesizeWithRetryAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"ElevenLabs synthesis failed with {(int)response.StatusCode}: {ExtractError(body)}");
        }

        var pcm = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await WritePcmWavAsync(outputPath, pcm, cancellationToken);
    }

    private async Task<string> ResolveClonedVoiceAsync(string speakerLabel, string referenceAudioPath, CancellationToken cancellationToken)
    {
        if (_clonedVoices.TryGetValue(speakerLabel, out var existing))
        {
            return existing;
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent($"Dubbing {speakerLabel}"), "name");
        form.Add(new StringContent("Speaker voice clone for this dubbing job"), "description");
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(referenceAudioPath, cancellationToken));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "files", Path.GetFileName(referenceAudioPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_secrets.ElevenLabsBaseUrl.TrimEnd('/')}/v1/voices/add")
        {
            Content = form
        };
        request.Headers.Add("xi-api-key", _secrets.ElevenLabsApiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ElevenLabs voice cloning failed with {(int)response.StatusCode}: {ExtractError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var voiceId = document.RootElement.GetProperty("voice_id").GetString();
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new InvalidOperationException("ElevenLabs voice cloning returned no voice ID.");
        }

        return _clonedVoices.GetOrAdd(speakerLabel, voiceId);
    }

    private static async Task WritePcmWavAsync(string outputPath, byte[] pcm, CancellationToken cancellationToken)
    {
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferBytes, useAsync: true);
        await file.WriteAsync(Encoding.ASCII.GetBytes("RIFF"), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(36 + pcm.Length), cancellationToken);
        await file.WriteAsync(Encoding.ASCII.GetBytes("WAVEfmt "), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(16), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes((short)1), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(channels), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(sampleRate), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(byteRate), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(blockAlign), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(bitsPerSample), cancellationToken);
        await file.WriteAsync(Encoding.ASCII.GetBytes("data"), cancellationToken);
        await file.WriteAsync(BitConverter.GetBytes(pcm.Length), cancellationToken);
        await file.WriteAsync(pcm, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendSynthesizeWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsTransient(response.StatusCode))
                {
                    var code = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"ElevenLabs returned {(HttpStatusCode)code}.");
                }

                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1));
                // ElevenLabs free tier rate-limits; back off instead of spamming.
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new HttpRequestException($"ElevenLabs synthesis failed after {MaxAttempts} attempts.", last);
    }

    private string ResolveVoice(string speakerLabel)
    {
        var match = System.Text.RegularExpressions.Regex.Match(speakerLabel, @"(\d+)");
        var index = match.Success ? int.Parse(match.Groups[1].Value) : speakerLabel.GetHashCode();
        return _voiceIds[Math.Abs(index) % _voiceIds.Length];
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? body;
                }

                if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
            // fall through to raw body
        }

        return body.Length <= 512 ? body : body[..512];
    }
}

public sealed class AzureSpeechVoiceProvider : IVoiceProvider
{
    private readonly HttpClient _http;
    private readonly AiSecretsOptions _secrets;

    public AzureSpeechVoiceProvider(HttpClient http, AiSecretsOptions secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    public string Name => "AzureSpeech";
    public async Task SynthesizeAsync(string text, string speakerLabel, string language, string outputPath, double targetDurationSeconds, CancellationToken cancellationToken, string? referenceAudioPath = null)
    {
        if (string.IsNullOrWhiteSpace(_secrets.AzureSpeechKey))
        {
            throw new InvalidOperationException("Azure Speech is not configured. Set AiSecrets:AzureSpeechKey and AzureSpeechRegion.");
        }

        var locale = language.Contains('-', StringComparison.Ordinal) ? language : $"{language}-US";
        var voice = locale.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR-DeniseNeural" : _secrets.AzureSpeechVoice;
        var escaped = System.Security.SecurityElement.Escape(text) ?? string.Empty;
        var ssml = $"<speak version=\"1.0\" xml:lang=\"{locale}\" xmlns=\"http://www.w3.org/2001/10/synthesis\"><voice name=\"{voice}\">{escaped}</voice></speak>";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_secrets.AzureSpeechRegion}.tts.speech.microsoft.com/cognitiveservices/v1")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml")
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", _secrets.AzureSpeechKey);
        request.Headers.Add("User-Agent", "VideoDubbingPlatform");
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-16khz-16bit-mono-pcm");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure Speech synthesis failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }
}

public sealed class CoquiVoiceProvider : IVoiceProvider
{
    private readonly OpenSourceCliVoiceProvider _cli;

    public CoquiVoiceProvider(string cliPath) => _cli = new OpenSourceCliVoiceProvider(cliPath, "Coqui");
    public string Name => "Coqui";
    public Task SynthesizeAsync(string text, string speakerLabel, string language, string outputPath, double targetDurationSeconds, CancellationToken cancellationToken, string? referenceAudioPath = null)
        => _cli.SynthesizeAsync(text, speakerLabel, language, outputPath, targetDurationSeconds, cancellationToken, referenceAudioPath);
}

public sealed class OpenVoiceProvider : IVoiceProvider
{
    private readonly OpenSourceCliVoiceProvider _cli;

    public OpenVoiceProvider(string cliPath) => _cli = new OpenSourceCliVoiceProvider(cliPath, "OpenVoice");
    public string Name => "OpenVoice";
    public Task SynthesizeAsync(string text, string speakerLabel, string language, string outputPath, double targetDurationSeconds, CancellationToken cancellationToken, string? referenceAudioPath = null)
        => _cli.SynthesizeAsync(text, speakerLabel, language, outputPath, targetDurationSeconds, cancellationToken, referenceAudioPath);
}
