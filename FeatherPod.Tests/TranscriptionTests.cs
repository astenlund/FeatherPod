using FeatherPod.Shared.Services;

using static FeatherPod.Shared.Services.TranscriptionService;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class TranscriptionTests
{
    #region VTT Parsing

    [Fact]
    public void ParseVttCues_BasicVtt_ParsesCorrectly()
    {
        // Arrange
        var vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:04.000
            Hello and welcome.

            00:00:05.000 --> 00:00:08.500
            Today we're talking about podcasts.

            """;

        // Act
        var cues = TranscriptionService.ParseVttCues(vtt);

        // Assert
        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), cues[0].End);
        Assert.Equal("Hello and welcome.", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5), cues[1].Start);
        Assert.Equal(TimeSpan.Parse("00:00:08.500"), cues[1].End);
        Assert.Equal("Today we're talking about podcasts.", cues[1].Text);
    }

    [Fact]
    public void ParseVttCues_MultiLineCue_PreservesNewlines()
    {
        // Arrange
        var vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:04.000
            First line
            Second line

            """;

        // Act
        var cues = TranscriptionService.ParseVttCues(vtt);

        // Assert
        Assert.Single(cues);
        Assert.Equal("First line\nSecond line", cues[0].Text);
    }

    [Fact]
    public void ParseVttCues_EmptyInput_ReturnsEmpty()
    {
        // Arrange
        var vtt = "WEBVTT\n\n";

        // Act
        var cues = TranscriptionService.ParseVttCues(vtt);

        // Assert
        Assert.Empty(cues);
    }

    [Fact]
    public void ParseVttCues_ShortTimestampFormat_Parses()
    {
        // Arrange
        var vtt = """
            WEBVTT

            01:30.000 --> 02:00.000
            Short format timestamps.

            """;

        // Act
        var cues = TranscriptionService.ParseVttCues(vtt);

        // Assert
        Assert.Single(cues);
        Assert.Equal(TimeSpan.FromSeconds(90), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(120), cues[0].End);
    }

    #endregion

    #region VTT Timestamp Formatting

    [Fact]
    public void FormatVttTimestamp_FormatsCorrectly()
    {
        // Arrange / Act / Assert
        Assert.Equal("00:00:01.000", TranscriptionService.FormatVttTimestamp(TimeSpan.FromSeconds(1)));
        Assert.Equal("01:30:00.000", TranscriptionService.FormatVttTimestamp(TimeSpan.FromMinutes(90)));
        Assert.Equal("00:01:30.500", TranscriptionService.FormatVttTimestamp(TimeSpan.Parse("00:01:30.500")));
    }

    #endregion

    #region VTT Serialization

    [Fact]
    public void SerializeVtt_ProducesValidVtt()
    {
        // Arrange
        var cues = new List<VttCue>
        {
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "Hello."),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8), "World.")
        };

        // Act
        var result = TranscriptionService.SerializeVtt(cues);

        // Assert
        Assert.StartsWith("WEBVTT", result);
        Assert.Contains("00:00:01.000 --> 00:00:04.000", result);
        Assert.Contains("Hello.", result);
        Assert.Contains("00:00:05.000 --> 00:00:08.000", result);
        Assert.Contains("World.", result);
    }

    [Fact]
    public void SerializeVtt_RoundTrips()
    {
        // Arrange
        var original = new List<VttCue>
        {
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "First cue."),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), "Second cue.")
        };

        // Act
        var serialized = TranscriptionService.SerializeVtt(original);
        var parsed = TranscriptionService.ParseVttCues(serialized);

        // Assert
        Assert.Equal(original.Count, parsed.Count);
        for (var i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Start, parsed[i].Start);
            Assert.Equal(original[i].End, parsed[i].End);
            Assert.Equal(original[i].Text, parsed[i].Text);
        }
    }

    #endregion

    #region Chunk Calculation

    [Fact]
    public void CalculateChunks_ShortAudio_SingleChunk()
    {
        // Arrange
        var totalDuration = TimeSpan.FromMinutes(10);
        var chunkDuration = TimeSpan.FromMinutes(12);
        var overlap = TimeSpan.FromSeconds(30);

        // Act
        var chunks = TranscriptionService.CalculateChunks(totalDuration, chunkDuration, overlap);

        // Assert
        Assert.Single(chunks);
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.Equal(totalDuration, chunks[0].Duration);
        Assert.Equal(TimeSpan.Zero, chunks[0].OwnedStart);
        Assert.Equal(totalDuration, chunks[0].OwnedEnd);
    }

    [Fact]
    public void CalculateChunks_ExactlyTwoChunks()
    {
        // Arrange
        var totalDuration = TimeSpan.FromMinutes(24);
        var chunkDuration = TimeSpan.FromMinutes(12);
        var overlap = TimeSpan.FromSeconds(30);

        // Act
        var chunks = TranscriptionService.CalculateChunks(totalDuration, chunkDuration, overlap);

        // Assert
        Assert.Equal(2, chunks.Count);

        // Chunk 0: owns 0:00-12:00
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
        Assert.Equal(TimeSpan.Zero, chunks[0].OwnedStart);
        Assert.Equal(TimeSpan.FromMinutes(12), chunks[0].OwnedEnd);

        // Chunk 1: starts at 11:30, owns 12:00-24:00
        Assert.Equal(TimeSpan.FromMinutes(12) - overlap, chunks[1].Start);
        Assert.Equal(TimeSpan.FromMinutes(12), chunks[1].OwnedStart);
        Assert.Equal(totalDuration, chunks[1].OwnedEnd);
    }

    [Fact]
    public void CalculateChunks_ThreeChunks_OwnedRangesContiguous()
    {
        // Arrange
        var totalDuration = TimeSpan.FromMinutes(36);
        var chunkDuration = TimeSpan.FromMinutes(12);
        var overlap = TimeSpan.FromSeconds(30);

        // Act
        var chunks = TranscriptionService.CalculateChunks(totalDuration, chunkDuration, overlap);

        // Assert
        Assert.Equal(3, chunks.Count);

        // Owned ranges should be contiguous and cover the full duration
        Assert.Equal(TimeSpan.Zero, chunks[0].OwnedStart);
        Assert.Equal(chunks[0].OwnedEnd, chunks[1].OwnedStart);
        Assert.Equal(chunks[1].OwnedEnd, chunks[2].OwnedStart);
        Assert.Equal(totalDuration, chunks[2].OwnedEnd);
    }

    [Fact]
    public void CalculateChunks_FirstChunkHasNoOverlapAtStart()
    {
        // Arrange
        var totalDuration = TimeSpan.FromMinutes(30);
        var chunkDuration = TimeSpan.FromMinutes(12);
        var overlap = TimeSpan.FromSeconds(30);

        // Act
        var chunks = TranscriptionService.CalculateChunks(totalDuration, chunkDuration, overlap);

        // Assert
        Assert.Equal(TimeSpan.Zero, chunks[0].Start);
    }

    #endregion

    #region VTT Stitching (Offset + Filter)

    [Fact]
    public void VttStitching_OverlapCuesFiltered()
    {
        // Arrange - simulate two chunks with overlap
        var chunkDuration = TimeSpan.FromMinutes(5);
        var overlap = TimeSpan.FromSeconds(10);
        var totalDuration = TimeSpan.FromMinutes(10);

        var chunks = TranscriptionService.CalculateChunks(totalDuration, chunkDuration, overlap);
        Assert.Equal(2, chunks.Count);

        // Chunk 0 VTT: cues at 0:30, 2:00, 4:50 (near boundary)
        var chunk0Cues = new List<VttCue>
        {
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35), "Early cue."),
            new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5), "Middle cue."),
            new(TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(295), "Near boundary.")
        };

        // Chunk 1 VTT: cues at 0:05 (in overlap region), 0:15 (past boundary), 2:00
        // Chunk 1 starts at 4:50, so absolute: 4:55, 5:05, 6:50
        var chunk1Cues = new List<VttCue>
        {
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), "Overlap region."),
            new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), "Past boundary."),
            new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5), "Later cue.")
        };

        // Act - apply offset and filter
        var allCues = new List<VttCue>();

        foreach (var cue in chunk0Cues)
        {
            var offset = cue with { Start = cue.Start + chunks[0].Start, End = cue.End + chunks[0].Start };
            if (offset.Start >= chunks[0].OwnedStart && offset.Start < chunks[0].OwnedEnd)
            {
                allCues.Add(offset);
            }
        }

        foreach (var cue in chunk1Cues)
        {
            var offset = cue with { Start = cue.Start + chunks[1].Start, End = cue.End + chunks[1].Start };
            if (offset.Start >= chunks[1].OwnedStart && offset.Start < chunks[1].OwnedEnd)
            {
                allCues.Add(offset);
            }
        }

        // Assert
        // Chunk 0: all 3 cues should be kept (all start before 5:00)
        // Chunk 1: "Overlap region" at abs 4:55 is before owned start 5:00 -> filtered
        //          "Past boundary" at abs 5:05 is in owned range -> kept
        //          "Later cue" at abs 6:50 is in owned range -> kept
        Assert.Equal(5, allCues.Count);
        Assert.Equal("Early cue.", allCues[0].Text);
        Assert.Equal("Middle cue.", allCues[1].Text);
        Assert.Equal("Near boundary.", allCues[2].Text);
        Assert.Equal("Past boundary.", allCues[3].Text);
        Assert.Equal("Later cue.", allCues[4].Text);
    }

    #endregion
}
