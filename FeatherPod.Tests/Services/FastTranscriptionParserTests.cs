using System.Text.Json;
using FeatherPod.Server.Services;

namespace FeatherPod.Tests.Services;

public class FastTranscriptionParserTests
{
    [Fact]
    public void Parse_RealFixture_ReturnsAllPhrasesWithSpeakerLabels()
    {
        // Arrange — captured response from the preflight smoke test against featherpod-speech.
        var json = LoadFixture();
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Equal(187, segments.Count);
        Assert.All(segments, s => Assert.StartsWith("Speaker ", s.SpeakerId));
        Assert.True(segments.Select(s => s.SpeakerId).Distinct().Count() >= 2, "Expected multi-speaker output");
        var first = segments[0];
        Assert.Equal("Speaker 1", first.SpeakerId);
        Assert.StartsWith("Imagine trying to build the Hoover Dam", first.Text);
        // First phrase: offsetMilliseconds = 120, durationMilliseconds = 6640.
        Assert.Equal(120 * TimeSpan.TicksPerMillisecond, first.OffsetTicks);
        Assert.Equal(6640 * TimeSpan.TicksPerMillisecond, first.DurationTicks);
    }

    [Fact]
    public void Parse_SinglePhrase_ProducesOneSegment()
    {
        // Arrange
        var json = """
            {
              "phrases": [
                { "speaker": 0, "offsetMilliseconds": 1000, "durationMilliseconds": 2500, "text": "Hello world." }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Speaker 0", only.SpeakerId);
        Assert.Equal("Hello world.", only.Text);
        Assert.Equal(1000 * TimeSpan.TicksPerMillisecond, only.OffsetTicks);
        Assert.Equal(2500 * TimeSpan.TicksPerMillisecond, only.DurationTicks);
    }

    [Fact]
    public void Parse_EmptyPhrasesArray_ReturnsEmptyList()
    {
        // Arrange
        var json = """{ "phrases": [] }""";
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_MissingPhrasesKey_ReturnsEmptyList()
    {
        // Arrange
        var json = """{ "durationMilliseconds": 1000 }""";
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_MissingSpeaker_DefaultsToSpeakerZero()
    {
        // Arrange — diarization disabled responses omit the speaker field entirely.
        var json = """
            {
              "phrases": [
                { "offsetMilliseconds": 0, "durationMilliseconds": 500, "text": "Solo." }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        Assert.Equal("Speaker 0", segments[0].SpeakerId);
    }

    [Fact]
    public void Parse_BlankText_PhraseIsSkipped()
    {
        // Arrange
        var json = """
            {
              "phrases": [
                { "speaker": 0, "offsetMilliseconds": 0, "durationMilliseconds": 100, "text": "   " },
                { "speaker": 1, "offsetMilliseconds": 100, "durationMilliseconds": 100, "text": "Real text." }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        // Act
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        // Assert
        var only = Assert.Single(segments);
        Assert.Equal("Real text.", only.Text);
    }

    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "fast-transcription-sample.json");

        return File.ReadAllText(path);
    }
}
