using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VideoDubbing.Infrastructure.Media;

internal static class FfmpegLocator
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(configured) || configured.IndexOfAny(PathSeparators) >= 0)
        {
            return configured;
        }

        return Cache.GetOrAdd(configured, static name => Locate(name) ?? name);
    }

    private static readonly char[] PathSeparators = { '/', '\\' };

    private static string? Locate(string name)
    {
        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".exe";

        foreach (var dir in SplitPath())
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        var winGetRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winGetRoot))
        {
            return null;
        }

        foreach (var package in Directory.EnumerateDirectories(winGetRoot, "Gyan.FFmpeg*"))
        {
            var hit = SearchBinDirectory(package, exe);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(IsWindowsPathSeparator ? ';' : ':',
                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsWindowsPathSeparator => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string? SearchBinDirectory(string root, string exe)
    {
        foreach (var bin in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var candidate = Path.Combine(bin, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}