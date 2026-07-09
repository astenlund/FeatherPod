using System.Text.Json;
using FeatherPod.Server.Services;

namespace FeatherPod.Tests.Services;

[Collection("Sequential")]
public class BatchTranscriptionParserTests
{
    [Fact]
    public void Parse_SinglePhrase_ProducesOneSegment()
    {
        // Arrange -- offsetInTicks/durationInTicks are floats in real batch responses.
        var json = """
            {
              "recognizedPhrases": [
                { "speaker": 1, "offsetInTicks": 400000.0, "durationInTicks": 25000000.0, "nBest": [ { "display": "Hello world." } ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Speaker 1", only.SpeakerId);
        Assert.Equal("Hello world.", only.Text);
        Assert.Equal(400000L, only.OffsetTicks);
        Assert.Equal(25000000L, only.DurationTicks);
    }

    [Fact]
    public void Parse_NBestWithMultipleCandidates_TakesFirstDisplay()
    {
        // Arrange
        var json = """
            {
              "recognizedPhrases": [
                {
                  "speaker": 2,
                  "offsetInTicks": 0.0,
                  "durationInTicks": 10000000.0,
                  "nBest": [ { "display": "Best candidate." }, { "display": "Worse candidate." } ]
                }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Best candidate.", only.Text);
    }

    [Fact]
    public void Parse_EmptyRecognizedPhrasesArray_ReturnsEmptyList()
    {
        // Arrange
        var json = """{ "recognizedPhrases": [] }""";
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_MissingRecognizedPhrasesKey_ReturnsEmptyList()
    {
        // Arrange
        var json = """{ "source": "https://example.test/audio.wav" }""";
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_MissingOffsetOrDuration_PhraseIsSkipped()
    {
        // Arrange
        var json = """
            {
              "recognizedPhrases": [
                { "speaker": 0, "durationInTicks": 10000000.0, "nBest": [ { "display": "No offset." } ] },
                { "speaker": 0, "offsetInTicks": 0.0, "nBest": [ { "display": "No duration." } ] },
                { "speaker": 1, "offsetInTicks": 10000000.0, "durationInTicks": 10000000.0, "nBest": [ { "display": "Complete." } ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Complete.", only.Text);
    }

    [Fact]
    public void Parse_MissingSpeaker_DefaultsToSpeakerZero()
    {
        // Arrange -- diarization disabled responses omit the speaker field entirely.
        var json = """
            {
              "recognizedPhrases": [
                { "offsetInTicks": 0.0, "durationInTicks": 5000000.0, "nBest": [ { "display": "Solo." } ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Equal("Speaker 0", segments[0].SpeakerId);
    }

    [Fact]
    public void Parse_BlankDisplay_PhraseIsSkipped()
    {
        // Arrange
        var json = """
            {
              "recognizedPhrases": [
                { "speaker": 0, "offsetInTicks": 0.0, "durationInTicks": 1000000.0, "nBest": [ { "display": "   " } ] },
                { "speaker": 1, "offsetInTicks": 1000000.0, "durationInTicks": 1000000.0, "nBest": [ { "display": "Real text." } ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Real text.", only.Text);
    }

    [Fact]
    public void Parse_IntegerTicks_ParsesViaGetDouble()
    {
        // Arrange -- ticks may also arrive as plain integers; GetDouble() handles both.
        var json = """
            {
              "recognizedPhrases": [
                { "speaker": 3, "offsetInTicks": 400000, "durationInTicks": 25000000, "nBest": [ { "display": "Integer ticks." } ] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = BatchTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal(400000L, only.OffsetTicks);
        Assert.Equal(25000000L, only.DurationTicks);
    }
}
