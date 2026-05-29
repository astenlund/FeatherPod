using System.Xml.Linq;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

public class RssFeedGeneratorTests
{
    [Fact]
    public void GenerateFeed_GuidIsNotMarkedAsPermaLink()
    {
        // Arrange
        var feed = new FeedConfig
        {
            Id = "test-feed",
            Title = "Test Podcast",
            Description = "A test podcast",
            Author = "Test Author"
        };
        var episode = new Episode
        {
            Id = "abc123def456",
            FeedId = "test-feed",
            Title = "Episode 1",
            FileName = "episode.mp3",
            FileSize = 1024,
            Source = UploadSource.Browser,
            UploadedAt = new DateTime(2026, 5, 28, 0, 0, 0, DateTimeKind.Utc)
        };
        var lastBuildDate = new DateTime(2026, 5, 28, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var xml = RssFeedGenerator.GenerateFeed(feed, "http://localhost", [episode], lastBuildDate);
        var doc = XDocument.Parse(xml);
        var guid = doc.Root!.Element("channel")!.Element("item")!.Element("guid")!;

        // Assert
        var isPermaLink = guid.Attribute("isPermaLink");
        Assert.NotNull(isPermaLink);
        Assert.Equal("false", isPermaLink.Value);
        Assert.Equal("abc123def456", guid.Value);
    }
}
