namespace VideoDubbing.Domain.Jobs;

public enum ProcessingStage
{
    Uploaded = 0,
    Validating = 1,
    ExtractingAudio = 2,
    Diarizing = 3,
    Transcribing = 4,
    Translating = 5,
    Synthesizing = 6,
    Synchronizing = 7,
    GeneratingSubtitles = 8,
    Muxing = 9,
    Completed = 10
}
