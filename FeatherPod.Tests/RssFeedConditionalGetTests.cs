using System.Xml.Linq;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

public class RssFeedConditionalGetTests
{
    [Fact]
    public void GenerateFeed_UsesProvidedLastBuildDate()
    {
        // Arrange
        var feed = new FeedConfig
        {
            Id = "test-feed",
            Title = "Test Podcast",
            Description = "A test podcast",
            Author = "Test Author"
        };

        var lastBuildDate = new DateTime(2026, 3, 15, 12, 30, 0, DateTimeKind.Utc);

        // Act
        var xml = RssFeedGenerator.GenerateFeed(feed, "http://localhost", [], lastBuildDate);
        var doc = XDocument.Parse(xml);
        var lastBuildDateElement = doc.Root!.Element("channel")!.Element("lastBuildDate")!.Value;

        // Assert
        Assert.Equal("Sun, 15 Mar 2026 12:30:00 GMT", lastBuildDateElement);
    }
}
