using FeatherPod.Server.Services;

namespace FeatherPod.Tests.Services;

public class VttSerializerTests
{
    [Fact]
    public void Serialize_BasicSegments_ProducesValidVtt()
    {
        // Arrange
        var segments = new List<DiarizedSegment>
        {
            new(TimeSpan.FromSeconds(1.2).Ticks, TimeSpan.FromSeconds(3.6).Ticks, "Speaker 1", "Welcome to the show."),
            new(TimeSpan.FromSeconds(5.1).Ticks, TimeSpan.FromSeconds(3.2).Ticks, "Speaker 2", "Thanks for having me.")
        };

        // Act
        var vtt = VttSerializer.Serialize(segments);

        // Assert
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.200 --> 00:00:04.800", vtt);
        Assert.Contains("<v Speaker 1>Welcome to the show.</v>", vtt);
        Assert.Contains("00:00:05.100 --> 00:00:08.300", vtt);
        Assert.Contains("<v Speaker 2>Thanks for having me.</v>", vtt);
    }

    [Fact]
    public void Serialize_EmptySegments_ReturnsHeaderOnly()
    {
        // Arrange
        var segments = new List<DiarizedSegment>();

        // Act
        var vtt = VttSerializer.Serialize(segments);

        // Assert
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Equal("WEBVTT\r\n\r\n", vtt);
    }

    [Fact]
    public void Serialize_MultipleSpeakers_LabelsCorrectly()
    {
        // Arrange
        var segments = new List<DiarizedSegment>
        {
            new(TimeSpan.FromSeconds(0).Ticks, TimeSpan.FromSeconds(2).Ticks, "Speaker 1", "Hello."),
            new(TimeSpan.FromSeconds(2).Ticks, TimeSpan.FromSeconds(2).Ticks, "Speaker 2", "Hi there."),
            new(TimeSpan.FromSeconds(4).Ticks, TimeSpan.FromSeconds(2).Ticks, "Speaker 1", "How are you?")
        };

        // Act
        var vtt = VttSerializer.Serialize(segments);

        // Assert
        var lines = vtt.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Contains("<v Speaker 1>Hello.</v>", lines);
        Assert.Contains("<v Speaker 2>Hi there.</v>", lines);
        Assert.Contains("<v Speaker 1>How are you?</v>", lines);
    }

    [Fact]
    public void FormatTimestamp_VariousValues_FormatsCorrectly()
    {
        // Arrange / Act / Assert
        Assert.Equal("00:00:01.000", VttSerializer.FormatTimestamp(TimeSpan.FromSeconds(1)));
        Assert.Equal("01:30:00.000", VttSerializer.FormatTimestamp(TimeSpan.FromMinutes(90)));
        Assert.Equal("00:01:30.500", VttSerializer.FormatTimestamp(TimeSpan.Parse("00:01:30.500")));
        Assert.Equal("00:00:00.000", VttSerializer.FormatTimestamp(TimeSpan.Zero));
    }

    [Fact]
    public void Serialize_LongTimestamps_FormatsCorrectly()
    {
        // Arrange
        var segments = new List<DiarizedSegment>
        {
            new(TimeSpan.FromHours(1).Ticks + TimeSpan.FromMinutes(23).Ticks + TimeSpan.FromSeconds(45.678).Ticks,
                TimeSpan.FromSeconds(5).Ticks,
                "Speaker 1",
                "Late in the episode.")
        };

        // Act
        var vtt = VttSerializer.Serialize(segments);

        // Assert
        Assert.Contains("01:23:45.678", vtt);
    }
}
