using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using FeatherPod.Models;
using FeatherPod.Services;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class RssFeedGeneratorTests
{
    private readonly IConfiguration _configuration;

    public RssFeedGeneratorTests()
    {
        var configData = new Dictionary<string, string>
        {
            ["Podcast:Title"] = "Test Podcast",
            ["Podcast:Description"] = "A test podcast",
            ["Podcast:Author"] = "Test Author",
            ["Podcast:Email"] = "test@example.com",
            ["Podcast:Language"] = "en-us",
            ["Podcast:Category"] = "Technology",
            ["Podcast:ImageUrl"] = "http://example.com/image.jpg",
            ["Podcast:BaseUrl"] = "http://localhost:5000"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
    }

    [Fact]
    public void GenerateFeed_ShouldContainChannelMetadata()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>();

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");

        // Assert
        var channel = doc.Root!.Element("channel");
        Assert.NotNull(channel);
        Assert.Equal("Test Podcast", channel.Element("title")!.Value);
        Assert.Equal("A test podcast", channel.Element("description")!.Value);
        Assert.Equal("en-us", channel.Element("language")!.Value);
        Assert.Equal("Test Author", channel.Element(ns + "author")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldContainItunesNamespace()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>();

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var rss = doc.Root!;
        var itunesNamespace = rss.GetNamespaceOfPrefix("itunes");
        Assert.NotNull(itunesNamespace);
        Assert.Equal("http://www.itunes.com/dtds/podcast-1.0.dtd", itunesNamespace.NamespaceName);
    }

    [Fact]
    public void GenerateFeed_ShouldIncludeEpisodes()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Episode 1",
                Description = "First episode",
                FileName = "ep1.mp3",
                FileSize = 12345,
                Duration = TimeSpan.FromMinutes(30),
                PublishedDate = DateTime.UtcNow
            },
            new Episode
            {
                Id = "episode2",
                Title = "Episode 2",
                Description = "Second episode",
                FileName = "ep2.mp3",
                FileSize = 23456,
                Duration = TimeSpan.FromMinutes(45),
                PublishedDate = DateTime.UtcNow.AddDays(-1)
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void GenerateFeed_ShouldOrderEpisodesByPublishedDateDescending()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var now = DateTime.UtcNow;
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Oldest",
                FileName = "ep1.mp3",
                FileSize = 100,
                PublishedDate = now.AddDays(-2)
            },
            new Episode
            {
                Id = "episode2",
                Title = "Newest",
                FileName = "ep2.mp3",
                FileSize = 100,
                PublishedDate = now
            },
            new Episode
            {
                Id = "episode3",
                Title = "Middle",
                FileName = "ep3.mp3",
                FileSize = 100,
                PublishedDate = now.AddDays(-1)
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Equal("Newest", items[0].Element("title")!.Value);
        Assert.Equal("Middle", items[1].Element("title")!.Value);
        Assert.Equal("Oldest", items[2].Element("title")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldIncludeEnclosureForEpisode()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Test Episode",
                FileName = "test.mp3",
                FileSize = 12345,
                PublishedDate = DateTime.UtcNow
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var enclosure = doc.Root!.Element("channel")!.Element("item")!.Element("enclosure");
        Assert.NotNull(enclosure);
        Assert.Equal("http://localhost:5000/audio/test.mp3", enclosure.Attribute("url")!.Value);
        Assert.Equal("12345", enclosure.Attribute("length")!.Value);
        Assert.Equal("audio/mpeg", enclosure.Attribute("type")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldSetCorrectMimeType_ForM4a()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Test Episode",
                FileName = "test.m4a",
                FileSize = 12345,
                PublishedDate = DateTime.UtcNow
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var enclosure = doc.Root!.Element("channel")!.Element("item")!.Element("enclosure");
        Assert.Equal("audio/mp4", enclosure!.Attribute("type")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldIncludeDuration()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Test Episode",
                FileName = "test.mp3",
                FileSize = 12345,
                Duration = new TimeSpan(1, 23, 45), // 1h 23m 45s
                PublishedDate = DateTime.UtcNow
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var duration = doc.Root!.Element("channel")!.Element("item")!.Element(ns + "duration");
        Assert.NotNull(duration);
        Assert.Equal("01:23:45", duration.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldIncludeOwnerInformation()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");
        var episodes = new List<Episode>();

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var owner = doc.Root!.Element("channel")!.Element(ns + "owner");
        Assert.NotNull(owner);
        Assert.Equal("Test Author", owner.Element(ns + "name")!.Value);
        Assert.Equal("test@example.com", owner.Element(ns + "email")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldIncludeCategory()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");
        var episodes = new List<Episode>();

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);
        var doc = XDocument.Parse(feedXml);

        // Assert
        var category = doc.Root!.Element("channel")!.Element(ns + "category");
        Assert.NotNull(category);
        Assert.Equal("Technology", category.Attribute("text")!.Value);
    }

    [Fact]
    public void GenerateFeed_ShouldBeValidXml()
    {
        // Arrange
        var generator = new RssFeedGenerator(_configuration);
        var episodes = new List<Episode>
        {
            new Episode
            {
                Id = "episode1",
                Title = "Test Episode",
                Description = "A test episode with <special> & characters",
                FileName = "test.mp3",
                FileSize = 12345,
                PublishedDate = DateTime.UtcNow
            }
        };

        // Act
        var feedXml = RssFeedGenerator.GenerateFeed(episodes);

        // Assert - Should not throw
        var exception = Record.Exception(() => XDocument.Parse(feedXml));
        Assert.Null(exception);
    }
}
