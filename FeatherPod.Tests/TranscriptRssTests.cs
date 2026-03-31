using System.Xml.Linq;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class TranscriptRssTests
{
    private static readonly XNamespace PodcastNs = "https://podcastindex.org/namespace/1.0";

    private static FeedConfig CreateFeed() => new()
    {
        Id = "test-feed",
        Title = "Test Podcast",
        Description = "A test podcast",
        Author = "Test Author"
    };

    [Fact]
    public void GenerateFeed_DeclaresPodcastNamespace()
    {
        // Arrange / Act
        var xml = RssFeedGenerator.GenerateFeed(CreateFeed(), "http://localhost", [], DateTime.UtcNow);
        var doc = XDocument.Parse(xml);

        // Assert
        var rss = doc.Root!;
        var podcastNs = rss.Attribute(XNamespace.Xmlns + "podcast");
        Assert.NotNull(podcastNs);
        Assert.Equal("https://podcastindex.org/namespace/1.0", podcastNs.Value);
    }

    [Fact]
    public void GenerateFeed_EpisodeWithTranscript_EmitsTranscriptTag()
    {
        // Arrange
        var episode = new Episode
        {
            Id = "abc123def456",
            FeedId = "test-feed",
            Title = "Test Episode",
            FileName = "test.m4a",
            FileSize = 1000,
            Source = UploadSource.Browser,
            UploadedAt = DateTime.UtcNow,
            TranscriptStatus = TranscriptStatus.Available
        };

        // Act
        var xml = RssFeedGenerator.GenerateFeed(CreateFeed(), "http://localhost", [episode], DateTime.UtcNow);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var transcript = item.Element(PodcastNs + "transcript");

        // Assert
        Assert.NotNull(transcript);
        Assert.Equal("http://localhost/test-feed/transcripts/abc123def456.vtt", transcript.Attribute("url")!.Value);
        Assert.Equal("text/vtt", transcript.Attribute("type")!.Value);
    }

    [Fact]
    public void GenerateFeed_EpisodeWithFailedTranscript_NoTranscriptTag()
    {
        // Arrange
        var episode = new Episode
        {
            Id = "abc123def456",
            FeedId = "test-feed",
            Title = "Test Episode",
            FileName = "test.m4a",
            FileSize = 1000,
            Source = UploadSource.Browser,
            UploadedAt = DateTime.UtcNow,
            TranscriptStatus = TranscriptStatus.Failed
        };

        // Act
        var xml = RssFeedGenerator.GenerateFeed(CreateFeed(), "http://localhost", [episode], DateTime.UtcNow);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var transcript = item.Element(PodcastNs + "transcript");

        // Assert
        Assert.Null(transcript);
    }

    [Fact]
    public void GenerateFeed_EpisodeWithNullTranscriptStatus_NoTranscriptTag()
    {
        // Arrange
        var episode = new Episode
        {
            Id = "abc123def456",
            FeedId = "test-feed",
            Title = "Test Episode",
            FileName = "test.m4a",
            FileSize = 1000,
            Source = UploadSource.Browser,
            UploadedAt = DateTime.UtcNow,
            TranscriptStatus = null
        };

        // Act
        var xml = RssFeedGenerator.GenerateFeed(CreateFeed(), "http://localhost", [episode], DateTime.UtcNow);
        var doc = XDocument.Parse(xml);
        var item = doc.Root!.Element("channel")!.Element("item")!;
        var transcript = item.Element(PodcastNs + "transcript");

        // Assert
        Assert.Null(transcript);
    }
}
