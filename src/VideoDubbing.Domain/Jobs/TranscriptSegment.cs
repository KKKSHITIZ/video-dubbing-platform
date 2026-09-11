namespace VideoDubbing.Domain.Jobs;

public sealed class TranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid? SpeakerId { get; set; }
    public int Sequence { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string SourceText { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    /// <summary>JSON map of target language code to translated text.</summary>
    public string TranslationsJson { get; set; } = "{}";
    public Job? Job { get; set; }
    public Speaker? Speaker { get; set; }
}
