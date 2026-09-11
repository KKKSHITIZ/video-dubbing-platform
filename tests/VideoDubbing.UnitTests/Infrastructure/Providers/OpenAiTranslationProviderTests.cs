using FluentAssertions;
using VideoDubbing.Application.Providers;
using VideoDubbing.Infrastructure.Providers.Translation;

namespace VideoDubbing.UnitTests.Infrastructure.Providers;

/// <summary>
/// Covers the id-aligned parsing of the context-aware OpenAI translation output: structured
/// objects, bare arrays, markdown fences, duplicate utterances, and missing/empty fallbacks.
/// </summary>
public sealed class OpenAiTranslationProviderTests
{
    private static readonly TranslationSegment[] Batch =
    [
        new("hello", "SPEAKER_00", 0, 1),
        new("yes", "SPEAKER_01", 1, 2)
    ];

    [Fact]
    public void ParseTranslations_maps_by_echoed_ids_in_order()
    {
        const string json = """{ "translations": [ { "id": "S1", "text": "[oui]" }, { "id": "S0", "text": "bonjour" } ] }""";

        OpenAiTranslationProvider.ParseTranslations(json, Batch).Should().Equal("bonjour", "[oui]");
    }

    [Fact]
    public void ParseTranslations_strips_markdown_code_fence()
    {
        const string json = "```json\n{ \"translations\": [ { \"id\": \"S0\", \"text\": \"bonjour\" }, { \"id\": \"S1\", \"text\": \"oui\" } ] }\n```";

        OpenAiTranslationProvider.ParseTranslations(json, Batch).Should().Equal("bonjour", "oui");
    }

    [Fact]
    public void ParseTranslations_accepts_bare_array_ordered_by_position()
    {
        const string json = """["bonjour", "oui"]""";

        OpenAiTranslationProvider.ParseTranslations(json, Batch).Should().Equal("bonjour", "oui");
    }

    [Fact]
    public void ParseTranslations_falls_back_to_source_when_alignment_breaks()
    {
        const string json = """{ "translations": [ { "id": "S0", "text": "bonjour" } ] }""";

        OpenAiTranslationProvider.ParseTranslations(json, Batch).Should().Equal("bonjour", "yes");
    }

    [Fact]
    public void ParseTranslations_returns_sources_for_unparseable_output()
    {
        OpenAiTranslationProvider.ParseTranslations("I am sorry I cannot do that.", Batch).Should().Equal("hello", "yes");
    }

    [Fact]
    public void ParseTranslations_handles_empty_entries_as_fallback()
    {
        const string json = """{ "translations": [ { "id": "S0", "text": "" }, { "id": "S1", "text": "oui" } ] }""";

        OpenAiTranslationProvider.ParseTranslations(json, Batch).Should().Equal("hello", "oui");
    }
}