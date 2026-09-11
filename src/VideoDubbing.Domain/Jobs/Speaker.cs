namespace VideoDubbing.Domain.Jobs;

public sealed class Speaker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string Label { get; set; } = string.Empty;
    public double SpeakingDurationSeconds { get; set; }
    public string VoiceId { get; set; } = string.Empty;
    public string? CharacteristicsJson { get; set; }
    public Job? Job { get; set; }
}
