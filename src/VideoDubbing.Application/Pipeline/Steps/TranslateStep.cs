using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoDubbing.Application.Providers;
using VideoDubbing.Domain.Jobs;

namespace VideoDubbing.Application.Pipeline.Steps;

public sealed class TranslateStep : IPipelineStep
{
    private readonly IProviderResolver _providers;
    private readonly ILogger<TranslateStep> _logger;

    public TranslateStep(IProviderResolver providers, ILogger<TranslateStep> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public string Name => "Translate";
    public ProcessingStage Stage => ProcessingStage.Translating;
    public int ProgressPercent => 55;

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        // Caption-only modes (Subtitles / Transcript) never translate — the transcript *is* the output.
        if (context.Job.ModeKind.IsCaptionOnly())
        {
            return;
        }

        var source = string.IsNullOrWhiteSpace(context.Job.DetectedLanguage) ? "en" : context.Job.DetectedLanguage;
        var segments = context.Transcript
            .Select(t => new TranslationSegment(t.Text, t.SpeakerLabel, t.StartSeconds, t.EndSeconds))
            .ToList();
        var duration = context.Job.DurationSeconds ?? 0;
        var theme = context.Job.ModeKind == JobMode.Remix ? context.Job.Theme : null;

        foreach (var language in context.Job.TargetLanguages)
        {
            IReadOnlyList<string>? translated = null;
            Exception? last = null;
            foreach (var provider in _providers.TranslationChain(duration))
            {
                try
                {
                    translated = await provider.TranslateAsync(
                        new TranslationRequest(segments, source, language, theme),
                        cancellationToken);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "Translation provider {Provider} failed for {Language} (segment count {Segments}).", provider.Name, language, context.Transcript.Count);
                }
            }

            if (translated is null)
            {
                throw last ?? new InvalidOperationException($"Translation to {language} failed.");
            }

            var translatedTurns = new List<Media.TranscriptTurn>();
            for (var i = 0; i < context.Transcript.Count; i++)
            {
                var original = context.Transcript[i];
                var translatedText = i < translated.Count ? translated[i] : original.Text;
                translatedText = StripMarkup(translatedText);
                translatedTurns.Add(original with { Text = translatedText, Language = language });

                var segment = context.Job.Segments.ElementAt(i);
                var current = JsonSerializer.Deserialize<Dictionary<string, string>>(segment.TranslationsJson)
                              ?? new Dictionary<string, string>();
                current[language] = translatedText;
                segment.TranslationsJson = JsonSerializer.Serialize(current);
            }

            context.Translations[language] = translatedTurns;
        }
    }

    /// <summary>Some TTS caches pollute translations with SSML markup (e.g. &lt;g&gt; markers from MyMemory's community corpus). Strip any tag and decode entities so transcripts/subtitles stay clean.</summary>
    private static string StripMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(cleaned).Trim();
    }
}
