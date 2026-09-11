using VideoDubbing.Application.Localization;
using VideoDubbing.Infrastructure.Localization;

namespace VideoDubbing.UnitTests.Localization;

public class ElevenLabsTranscriptParserTests
{
    [Fact]
    public void ParseTranscriptJson_NewSchema_GroupsSpeakerWise()
    {
        const string json = """
        {
          "dubbing_id": "abc",
          "transcript": [
            { "speaker_id": "speaker_0", "translated_text": "Bonjour à toutes et à tous.", "start": 0.12, "end": 2.40, "type": "sentence" },
            { "speaker_id": "speaker_1", "translated_text": "Parlons de la traduction de vidéo.", "start": 2.80, "end": 5.10, "type": "sentence" },
            { "speaker_id": "speaker_0", "translated_text": "C'est tout.", "start": 5.30, "end": 6.00, "type": "sentence" }
          ]
        }
        """;

        var segments = ElevenLabsDubbingApi.ParseTranscriptJson(json);

        Assert.Equal(3, segments.Count);
        Assert.Equal("speaker_0", segments[0].Speaker);
        Assert.Equal("Bonjour à toutes et à tous.", segments[0].Text);
        Assert.Equal(0.12, segments[0].StartSeconds, 3);
        Assert.Equal(2.40, segments[0].EndSeconds, 3);
        Assert.Equal("speaker_1", segments[1].Speaker);
        Assert.Equal("speaker_0", segments[2].Speaker);
    }

    [Fact]
    public void ParseTranscriptJson_LegacyParagraphsSchema_Works()
    {
        const string json = """
        {
          "paragraphs": [
            {
              "speaker": "SPEAKER_00",
              "sentences": [
                { "offset": 0, "start_time": "00:00:00,120", "end_time": "00:00:02,400", "text": "Olá a todos." }
              ]
            },
            {
              "speaker": "SPEAKER_01",
              "sentences": [
                { "offset": 2, "start_time": "00:00:02,800", "end_time": "00:00:05,100", "text": "Falamos sobre a tradução." }
              ]
            }
          ]
        }
        """;

        var segments = ElevenLabsDubbingApi.ParseTranscriptJson(json);

        Assert.Equal(2, segments.Count);
        Assert.Equal("SPEAKER_00", segments[0].Speaker);
        Assert.Equal("Olá a todos.", segments[0].Text);
        Assert.Equal("SPEAKER_01", segments[1].Speaker);
        Assert.Equal("Falamos sobre a tradução.", segments[1].Text);
    }

    [Fact]
    public void ParseTranscriptJson_EmptyOrUnknown_ReturnsEmpty()
    {
        Assert.Empty(ElevenLabsDubbingApi.ParseTranscriptJson("{}"));
        Assert.Empty(ElevenLabsDubbingApi.ParseTranscriptJson("not json"));
    }

    [Theory]
    [InlineData("fr", "French")]
    [InlineData("es", "Español")]
    [InlineData("en", "English")]
    [InlineData("xx", "xx")]
    [InlineData(null, "")]
    public void LanguageNames_DisplayName_MapsKnownCodes(string? code, string expected)
    {
        Assert.Equal(expected, LanguageNames.DisplayName(code));
    }
}