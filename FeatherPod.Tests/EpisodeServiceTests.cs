using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class EpisodeServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EpisodeService> _logger;
    private const string TestFeedId = "test-feed";

    private readonly List<EpisodeService> _servicesToDispose = [];
    private readonly List<TestBlobStorageService> _blobServicesToDispose = [];

    public EpisodeServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");

        Directory.CreateDirectory(_testDirectory);

        // Create logger that suppresses warnings from test dummy files
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Error); // Only show errors and above
        });

        _logger = _loggerFactory.CreateLogger<EpisodeService>();
    }

    private EpisodeService CreateService()
    {
        var blobStorage = new TestBlobStorageService(_testDirectory);
        _blobServicesToDispose.Add(blobStorage);

        var service = new EpisodeService(blobStorage, _logger);
        _servicesToDispose.Add(service);

        return service;
    }

    private static async Task CreateTestFeedAsync(EpisodeService service, string feedId = TestFeedId)
    {
        var feedConfig = new FeedConfig
        {
            Id = feedId,
            Title = "Test Podcast",
            Description = "Test Description",
            Author = "Test Author",
            Email = "test@example.com",
            Language = "en",
            Category = "Technology"
        };

        await service.CreateFeedAsync(feedConfig);
    }

    [Fact]
    public async Task InitializeAsync_ShouldLoadExistingFeeds()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.InitializeAsync();
        await CreateTestFeedAsync(service, "feed1");
        await CreateTestFeedAsync(service, "feed2");

        // Create a new service instance to test loading
        var service2 = CreateService();
        await service2.InitializeAsync();
        var feeds = await service2.GetFeedsAsync();

        // Assert
        Assert.Equal(2, feeds.Count);
        Assert.Contains(feeds, f => f.Id == "feed1");
        Assert.Contains(feeds, f => f.Id == "feed2");
    }

    [Fact]
    public async Task GetAllEpisodesAsync_ShouldReturnEpisodesOrderedByPublishedDate()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        // Create test files
        var file1 = Path.Combine(_testDirectory, "test1.mp3");
        var file2 = Path.Combine(_testDirectory, "test2.mp3");
        await File.WriteAllTextAsync(file1, "audio1");
        await File.WriteAllTextAsync(file2, "audio2");

        // Act
        await service.AddEpisodeAsync(TestFeedId, file1, "Episode 1");
        await Task.Delay(10); // Ensure different timestamps
        await service.AddEpisodeAsync(TestFeedId, file2, "Episode 2");

        var episodes = await service.GetAllEpisodesAsync(TestFeedId);

        // Assert
        Assert.Equal(2, episodes.Count);
        Assert.Equal("Episode 2", episodes[0].Title); // Most recent first
        Assert.Equal("Episode 1", episodes[1].Title);
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldGenerateConsistentId()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");
        var fileSize = new FileInfo(testFile).Length;
        var expectedId = Episode.GenerateId(TestFeedId, "test.mp3", fileSize);

        // Act
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Assert
        Assert.Equal(expectedId, episode.Id);
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldUploadToBlobStorage()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Assert
        var blobStorage = _blobServicesToDispose[0];
        var exists = await blobStorage.AudioExistsAsync(TestFeedId, "test.mp3");
        Assert.True(exists);
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldReplaceMetadata_WhenSameFileAlreadyExists()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        var firstEpisode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test 1");
        var secondEpisode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test 2"); // Re-upload with new title

        var episodes = await service.GetAllEpisodesAsync(TestFeedId);

        // Assert
        Assert.Single(episodes); // Only one episode exists
        Assert.Equal("Test 2", episodes[0].Title); // Metadata replaced
        Assert.Equal(firstEpisode.Id, secondEpisode.Id); // Episode ID preserved
    }

    [Fact]
    public async Task DeleteEpisodeAsync_ShouldRemoveEpisodeAndFile()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Act
        var result = await service.DeleteEpisodeAsync(TestFeedId, episode.Id);

        // Assert
        Assert.True(result);
        var episodes = await service.GetAllEpisodesAsync(TestFeedId);
        Assert.Empty(episodes);

        var blobStorage = _blobServicesToDispose[0];
        var exists = await blobStorage.AudioExistsAsync(TestFeedId, "test.mp3");
        Assert.False(exists);
    }

    [Fact]
    public async Task DeleteEpisodeAsync_ShouldReturnFalse_WhenEpisodeNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        // Act
        var result = await service.DeleteEpisodeAsync(TestFeedId, "nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SyncWithBlobStorageAsync_ShouldRemoveEpisodesWithMissingFiles()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        _ = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Delete the audio file manually from blob storage
        var blobStorage = _blobServicesToDispose[0];
        await blobStorage.DeleteAudioAsync(TestFeedId, "test.mp3");

        // Act
        await service.SyncWithBlobStorageAsync(TestFeedId);

        var episodes = await service.GetAllEpisodesAsync(TestFeedId);

        // Assert
        Assert.Empty(episodes);
    }

    [Fact]
    public async Task GetEpisodeByIdAsync_ShouldReturnCorrectEpisode()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var addedEpisode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Act
        var episode = await service.GetEpisodeByIdAsync(TestFeedId, addedEpisode.Id);

        // Assert
        Assert.NotNull(episode);
        Assert.Equal("Test", episode.Title);
    }

    [Fact]
    public async Task GetEpisodeByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        // Act
        var episode = await service.GetEpisodeByIdAsync(TestFeedId, "nonexistent");

        // Assert
        Assert.Null(episode);
    }

    [Fact]
    public async Task Episodes_ShouldBeIsolatedBetweenFeeds()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service, "feed1");
        await CreateTestFeedAsync(service, "feed2");

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        await service.AddEpisodeAsync("feed1", testFile, "Feed 1 Episode");

        var feed1Episodes = await service.GetAllEpisodesAsync("feed1");
        var feed2Episodes = await service.GetAllEpisodesAsync("feed2");

        // Assert
        Assert.Single(feed1Episodes);
        Assert.Empty(feed2Episodes);
    }

    public void Dispose()
    {
        // Dispose all services first to release file handles
        foreach (var service in _servicesToDispose)
        {
            service.Dispose();
        }
        _servicesToDispose.Clear();
        _blobServicesToDispose.Clear();

        // Give async operations time to complete
        Thread.Sleep(100);

        // Retry directory deletion to handle any remaining file locks
        if (Directory.Exists(_testDirectory))
        {
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(_testDirectory, recursive: true);
                    break;
                }
                catch (IOException) when (i < 2)
                {
                    Thread.Sleep(100);
                }
                catch
                {
                    // Ignore cleanup errors on final attempt
                }
            }
        }
    }
}

public class TitleParsingTests
{
    [Theory]
    [InlineData("Simple_Title.m4a", "Simple Title")]
    [InlineData("Multiple_Words_Here.mp3", "Multiple Words Here")]
    public void ParseTitleFromFilename_ReplacesUnderscoresWithSpaces(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Cold_War_Built_Silicon_Valley_s_Wealth.m4a", "Cold War Built Silicon Valley's Wealth")]
    [InlineData("America_s_Best.m4a", "America's Best")]
    [InlineData("Today_s_Episode.mp3", "Today's Episode")]
    public void ParseTitleFromFilename_ConvertsPossessives(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Unlocking_GaussianImage__The_Speed_vs.m4a", "Unlocking Gaussian Image: The Speed vs")]
    [InlineData("Topic__Subtitle.mp3", "Topic: Subtitle")]
    [InlineData("Main_Title__Sub_Title.m4a", "Main Title: Sub Title")]
    public void ParseTitleFromFilename_ConvertsDoubleUnderscoreToColon(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("PascalCaseTitle.m4a", "Pascal Case Title")]
    [InlineData("NotebookLMOverview.mp3", "Notebook LMOverview")]
    public void ParseTitleFromFilename_HandlesPascalCase(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("America_s_Future__A_Deep_Dive.m4a", "America's Future: A Deep Dive")]
    public void ParseTitleFromFilename_HandlesCombinedPatterns(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Valve%E2%80%99s_Steam_Frame_Unpacked__Why_Their_Wireless-Only%2C_Modular_.m4a", "Valve's Steam Frame Unpacked: Why Their Wireless-Only, Modular")]
    [InlineData("Hello%20World.mp3", "Hello World")]
    public void ParseTitleFromFilename_DecodesUrlEncodedCharacters(string fileName, string expected)
    {
        var result = EpisodeService.ParseTitleFromFilename(fileName);
        Assert.Equal(expected, result);
    }
}
