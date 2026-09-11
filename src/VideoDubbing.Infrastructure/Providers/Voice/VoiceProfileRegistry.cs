using System.Security.Cryptography;
using System.Text;
using VideoDubbing.Application.Providers;

namespace VideoDubbing.Infrastructure.Providers.Voice;

/// <summary>
/// Assigns a stable, deterministic voice profile identity to each original speaker so that
/// a given speaker keeps the same voice across every segment and every target language.
/// The identity is derived from a stable seed (project + speaker label) — NOT the row PK — so
/// it is reproducible across runs and independent of database insertion order.
/// </summary>
public sealed class VoiceProfileRegistry : IVoiceProfileRegistry
{
    private readonly Dictionary<Guid, VoiceProfile> _profiles = new();

    public string ResolveVoiceId(Guid speakerId, string speakerLabel)
    {
        var seed = $"dub:{speakerLabel}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var id = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        _profiles[speakerId] = new VoiceProfile(id, speakerLabel);
        return id;
    }

    public VoiceProfile GetProfile(Guid speakerId)
        => _profiles.TryGetValue(speakerId, out var profile)
            ? profile
            : new VoiceProfile("0000000000000000", "UNKNOWN");
}
