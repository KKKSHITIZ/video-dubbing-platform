using System.Diagnostics;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.LipSync;

/// <summary>
/// Default no-op lip sync: returns the original video unchanged. Use this when no external
/// lip-sync engine is available; the time-aligned audio is still mixed via FFmpeg later.
/// </summary>
public sealed class PassthroughLipSyncEngine : ILipSyncEngine
{
    public string Name => "Passthrough";

    public Task<string> LipSyncAsync(
        string originalVideoPath,
        string targetAudioPath,
        string outputVideoPath,
        string voiceLabel,
        CancellationToken cancellationToken)
        => Task.FromResult(originalVideoPath);
}

/// <summary>
/// Best-effort wrapper around a Wav2Lip CLI (python inference.py) or Video-Retalking CLI.
/// The time-aligned target audio is handed to the engine together with the original video so
/// the mouth is re-aligned to the translated audio. Falls back to the input path on failure.
/// </summary>
public sealed class CliLipSyncEngine : ILipSyncEngine
{
    private readonly string _cliPath;

    public CliLipSyncEngine(string cliPath) => _cliPath = cliPath;
    public string Name => "Cli";

    public async Task<string> LipSyncAsync(
        string originalVideoPath,
        string targetAudioPath,
        string outputVideoPath,
        string voiceLabel,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cliPath,
            Arguments = $"--video \"{originalVideoPath.Replace("\"", "\\\"")}\" --audio \"{targetAudioPath.Replace("\"", "\\\"")}\" --out \"{outputVideoPath.Replace("\"", "\\\"")}\" --voice \"{voiceLabel}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start lip-sync CLI at {_cliPath}.");
            }

            await process.WaitForExitAsync(cancellationToken);
            return File.Exists(outputVideoPath) ? outputVideoPath : originalVideoPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Lip-sync engine failed ({_cliPath}); falling back to original video. {ex.Message}");
            return originalVideoPath;
        }
    }
}
