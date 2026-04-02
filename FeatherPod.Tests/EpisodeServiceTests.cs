using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
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
    public async Task AddEpisodeAsync_ShouldUseProvidedEpisodeId_WhenSupplied()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Use a custom episode ID (simulating CLI sending ID based on original file size)
        var customEpisodeId = "custom123456";

        // Act
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test", episodeId: customEpisodeId);

        // Assert
        Assert.Equal(customEpisodeId, episode.Id);
    }

    [Fact]
    public async Task AddEpisodeAsync_WithProvidedEpisodeId_ShouldReplaceExistingEpisode()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile1 = Path.Combine(_testDirectory, "test.mp3");
        var testFile2 = Path.Combine(_testDirectory, "test_normalized.mp3");
        await File.WriteAllTextAsync(testFile1, "original audio data");
        await File.WriteAllTextAsync(testFile2, "normalized audio"); // Different size

        // Both uploads use the same episodeId (simulating re-upload scenario)
        var sharedEpisodeId = "shared123456";

        // Act
        var firstEpisode = await service.AddEpisodeAsync(TestFeedId, testFile1, "First Upload", episodeId: sharedEpisodeId);
        var secondEpisode = await service.AddEpisodeAsync(TestFeedId, testFile2, "Second Upload", episodeId: sharedEpisodeId);

        var episodes = await service.GetAllEpisodesAsync(TestFeedId);

        // Assert
        Assert.Single(episodes); // Only one episode exists (replaced)
        Assert.Equal(sharedEpisodeId, episodes[0].Id);
        Assert.Equal("Second Upload", episodes[0].Title); // Title updated
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
    public async Task DeleteEpisodeAsync_ShouldPreserveFile_WhenOtherEpisodeSharesSameFile()
    {
        // Arrange - Create two episodes pointing to the same file (simulates re-upload with different file size after normalization)
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "shared.mp3");

        // First upload - creates episode with ID based on size "audio data 1" (12 bytes)
        await File.WriteAllTextAsync(testFile, "audio data 1");
        var episode1 = await service.AddEpisodeAsync(TestFeedId, testFile, "First Title");

        // Second upload - different size creates different ID, but same filename overwrites blob
        await File.WriteAllTextAsync(testFile, "audio data 2 - longer content");
        var episode2 = await service.AddEpisodeAsync(TestFeedId, testFile, "Second Title");

        // Verify we have two episodes with different IDs but same filename
        Assert.NotEqual(episode1.Id, episode2.Id);
        Assert.Equal(episode1.FileName, episode2.FileName);

        var episodesBefore = await service.GetAllEpisodesAsync(TestFeedId);
        Assert.Equal(2, episodesBefore.Count);

        // Act - Delete first episode
        var result = await service.DeleteEpisodeAsync(TestFeedId, episode1.Id);

        // Assert - File should still exist because episode2 references it
        Assert.True(result);
        var episodesAfter = await service.GetAllEpisodesAsync(TestFeedId);
        Assert.Single(episodesAfter);
        Assert.Equal(episode2.Id, episodesAfter[0].Id);

        var blobStorage = _blobServicesToDispose[0];
        var fileStillExists = await blobStorage.AudioExistsAsync(TestFeedId, "shared.mp3");
        Assert.True(fileStillExists, "Audio file should not be deleted when another episode references it");
    }

    [Fact]
    public async Task SyncWithBlobStorageAsync_ShouldPreserveEpisodesWithMissingFiles()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Delete the audio file manually from blob storage
        var blobStorage = _blobServicesToDispose[0];
        await blobStorage.DeleteAudioAsync(TestFeedId, "test.mp3");

        // Act
        await service.SyncWithBlobStorageAsync(TestFeedId);

        var episodes = await service.GetAllEpisodesAsync(TestFeedId);

        // Assert - episode should still exist (sync warns but doesn't delete to prevent silent data loss)
        Assert.Single(episodes);
        Assert.Equal(episode.Id, episodes[0].Id);
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

    [Fact]
    public async Task AddEpisodeAsync_ShouldSetSourceAndUploadedAt()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        var beforeUpload = DateTime.UtcNow;
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test", source: UploadSource.Browser);
        var afterUpload = DateTime.UtcNow;

        // Assert
        Assert.Equal(UploadSource.Browser, episode.Source);
        Assert.True(episode.UploadedAt >= beforeUpload && episode.UploadedAt <= afterUpload);
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldDefaultSourceToCLI()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Test");

        // Assert
        Assert.Equal(UploadSource.CLI, episode.Source);
        Assert.NotEqual(default, episode.UploadedAt);
    }

    [Fact]
    public async Task GetRecentUploadsAsync_ShouldOrderByUploadedAtDescending()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var file1 = Path.Combine(_testDirectory, "test1.mp3");
        var file2 = Path.Combine(_testDirectory, "test2.mp3");
        await File.WriteAllTextAsync(file1, "audio1");
        await File.WriteAllTextAsync(file2, "audio2");

        await service.AddEpisodeAsync(TestFeedId, file1, "Episode 1");
        await Task.Delay(50); // Ensure different timestamps
        await service.AddEpisodeAsync(TestFeedId, file2, "Episode 2");

        // Act
        var recentUploads = await service.GetRecentUploadsAsync(TestFeedId, null, 10);

        // Assert
        Assert.Equal(2, recentUploads.Count);
        Assert.Equal("Episode 2", recentUploads[0].Title); // Most recent first
        Assert.Equal("Episode 1", recentUploads[1].Title);
    }

    [Fact]
    public async Task GetRecentUploadsAsync_ShouldFilterBySource()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var file1 = Path.Combine(_testDirectory, "test1.mp3");
        var file2 = Path.Combine(_testDirectory, "test2.mp3");
        await File.WriteAllTextAsync(file1, "audio1");
        await File.WriteAllTextAsync(file2, "audio2");

        await service.AddEpisodeAsync(TestFeedId, file1, "CLI Episode", source: UploadSource.CLI);
        await service.AddEpisodeAsync(TestFeedId, file2, "Browser Episode", source: UploadSource.Browser);

        // Act
        var browserUploads = await service.GetRecentUploadsAsync(TestFeedId, UploadSource.Browser, 10);
        var cliUploads = await service.GetRecentUploadsAsync(TestFeedId, UploadSource.CLI, 10);

        // Assert
        Assert.Single(browserUploads);
        Assert.Equal("Browser Episode", browserUploads[0].Title);

        Assert.Single(cliUploads);
        Assert.Equal("CLI Episode", cliUploads[0].Title);
    }

    [Fact]
    public async Task GetRecentUploadsAsync_ShouldRespectLimit()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        for (var i = 1; i <= 5; i++)
        {
            var file = Path.Combine(_testDirectory, $"test{i}.mp3");
            await File.WriteAllTextAsync(file, $"audio{i}");
            await service.AddEpisodeAsync(TestFeedId, file, $"Episode {i}");
        }

        // Act
        var recentUploads = await service.GetRecentUploadsAsync(TestFeedId, null, 3);

        // Assert
        Assert.Equal(3, recentUploads.Count);
    }

    [Fact]
    public async Task UpdateEpisodeTitleAsync_ShouldUpdateTitle()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Original Title");

        // Act
        var updated = await service.UpdateEpisodeTitleAsync(TestFeedId, episode.Id, "New Title");

        // Assert
        Assert.NotNull(updated);
        Assert.Equal("New Title", updated.Title);

        var retrieved = await service.GetEpisodeByIdAsync(TestFeedId, episode.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("New Title", retrieved.Title);
    }

    [Fact]
    public async Task UpdateEpisodeTitleAsync_ShouldReturnNull_WhenEpisodeNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        // Act
        var result = await service.UpdateEpisodeTitleAsync(TestFeedId, "nonexistent", "New Title");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateEpisodeTitleAsync_ShouldPreserveOtherFields()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Original Title", source: UploadSource.Browser);

        // Act
        var updated = await service.UpdateEpisodeTitleAsync(TestFeedId, episode.Id, "New Title");

        // Assert
        Assert.NotNull(updated);
        Assert.Equal(episode.Id, updated.Id);
        Assert.Equal(episode.FeedId, updated.FeedId);
        Assert.Equal(episode.FileName, updated.FileName);
        Assert.Equal(episode.FileSize, updated.FileSize);
        Assert.Equal(episode.Duration, updated.Duration);
        Assert.Equal(episode.PublishedDate, updated.PublishedDate);
        Assert.Equal(episode.Source, updated.Source);
        Assert.Equal(episode.UploadedAt, updated.UploadedAt);
    }

    [Fact]
    public async Task GetRecentUploadsAsync_ShouldClampLimitToValidRange()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var file = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(file, "audio");
        await service.AddEpisodeAsync(TestFeedId, file, "Episode");

        // Act - request 100 episodes (exceeds max of 50)
        var result = await service.GetRecentUploadsAsync(TestFeedId, null, 100);

        // Assert - should work (clamped to 50) and return the one episode we have
        Assert.Single(result);
    }

    // Feed version tracking tests

    [Fact]
    public async Task GetFeedSnapshot_ReturnsNullForNonexistentFeed()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        // Act
        var snapshot = await service.GetFeedSnapshotAsync("nonexistent");

        // Assert
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task FeedVersion_IncreasesOnEpisodeAdd()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var snapshot1 = await service.GetFeedSnapshotAsync(TestFeedId);
        var versionAfterCreate = snapshot1!.Value.Version;

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        await service.AddEpisodeAsync(TestFeedId, testFile, "Episode 1");
        var snapshot2 = await service.GetFeedSnapshotAsync(TestFeedId);

        // Assert
        Assert.True(snapshot2!.Value.Version > versionAfterCreate);
    }

    [Fact]
    public async Task FeedVersion_IncreasesOnEpisodeDelete()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Episode 1");

        var snapshot1 = await service.GetFeedSnapshotAsync(TestFeedId);
        var versionBeforeDelete = snapshot1!.Value.Version;

        // Act
        await service.DeleteEpisodeAsync(TestFeedId, episode.Id);
        var snapshot2 = await service.GetFeedSnapshotAsync(TestFeedId);

        // Assert
        Assert.True(snapshot2!.Value.Version > versionBeforeDelete);
    }

    [Fact]
    public async Task FeedVersion_IncreasesOnMetadataUpdate()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");
        var episode = await service.AddEpisodeAsync(TestFeedId, testFile, "Episode 1");

        var snapshot1 = await service.GetFeedSnapshotAsync(TestFeedId);
        var versionBeforeUpdate = snapshot1!.Value.Version;

        // Act
        await service.UpdateEpisodeMetadataAsync(TestFeedId, episode.Id, title: "New Title");
        var snapshot2 = await service.GetFeedSnapshotAsync(TestFeedId);

        // Assert
        Assert.True(snapshot2!.Value.Version > versionBeforeUpdate);
    }

    [Fact]
    public async Task FeedVersion_IncreasesOnFeedConfigUpdate()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var snapshot1 = await service.GetFeedSnapshotAsync(TestFeedId);
        var versionBeforeUpdate = snapshot1!.Value.Version;

        // Act
        var updatedConfig = new FeedConfig
        {
            Id = TestFeedId,
            Title = "Updated Podcast",
            Description = "Updated Description",
            Author = "Test Author"
        };
        await service.UpdateFeedAsync(TestFeedId, updatedConfig);
        var snapshot2 = await service.GetFeedSnapshotAsync(TestFeedId);

        // Assert
        Assert.True(snapshot2!.Value.Version > versionBeforeUpdate);
    }

    [Fact]
    public async Task FeedVersion_SnapshotIncludesEpisodes()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();
        await CreateTestFeedAsync(service);

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");
        await service.AddEpisodeAsync(TestFeedId, testFile, "Episode 1");

        // Act
        var snapshot = await service.GetFeedSnapshotAsync(TestFeedId);

        // Assert
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Value.Episodes);
        Assert.Equal("Episode 1", snapshot.Value.Episodes[0].Title);
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
    [InlineData("Apple_s_iPad_Ad.m4a", "Apple's iPad Ad")]
    [InlineData("New_iPhone_Features.mp3", "New iPhone Features")]
    [InlineData("iOS_18_Review.m4a", "iOS 18 Review")]
    [InlineData("iPad_Pro_Review.m4a", "iPad Pro Review")]
    [InlineData("iPad_vs_MacBook.m4a", "iPad vs MacBook")]
    public void ParseTitleFromFilename_PreservesAppleBrandNames(string fileName, string expected)
    {
        // Arrange / Act
        var result = EpisodeService.ParseTitleFromFilename(fileName);

        // Assert
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

public class TitleGenerationTests
{
    private class StubAiService(bool isAvailable, string? suggestedTitle) : IAiService
    {
        public bool IsAvailable => isAvailable;

        public Task<string?> SuggestTitleAsync(string filename, string? note = null, CancellationToken cancellationToken = default)
            => Task.FromResult(suggestedTitle);
    }

    [Fact]
    public async Task GenerateTitleAsync_ReturnsAiTitle_WhenAvailableAndSucceeds()
    {
        // Arrange
        var aiService = new StubAiService(isAvailable: true, suggestedTitle: "AI Generated Title");

        // Act
        var result = await EpisodeService.GenerateTitleAsync("my_podcast_episode.mp3", aiService);

        // Assert
        Assert.Equal("AI Generated Title", result);
    }

    [Fact]
    public async Task GenerateTitleAsync_FallsBackToHeuristic_WhenAiReturnsNull()
    {
        // Arrange
        var aiService = new StubAiService(isAvailable: true, suggestedTitle: null);

        // Act
        var result = await EpisodeService.GenerateTitleAsync("my_podcast_episode.mp3", aiService);

        // Assert
        Assert.Equal("my podcast episode", result);
    }

    [Fact]
    public async Task GenerateTitleAsync_FallsBackToHeuristic_WhenAiReturnsEmpty()
    {
        // Arrange
        var aiService = new StubAiService(isAvailable: true, suggestedTitle: "");

        // Act
        var result = await EpisodeService.GenerateTitleAsync("my_podcast_episode.mp3", aiService);

        // Assert
        Assert.Equal("my podcast episode", result);
    }

    [Fact]
    public async Task GenerateTitleAsync_UsesHeuristic_WhenAiUnavailable()
    {
        // Arrange
        var aiService = new StubAiService(isAvailable: false, suggestedTitle: null);

        // Act
        var result = await EpisodeService.GenerateTitleAsync("my_podcast_episode.mp3", aiService);

        // Assert
        Assert.Equal("my podcast episode", result);
    }

    [Fact]
    public async Task GenerateTitleAsync_FallsBackToHeuristic_WhenAiReturnsWhitespace()
    {
        // Arrange
        var aiService = new StubAiService(isAvailable: true, suggestedTitle: "   ");

        // Act
        var result = await EpisodeService.GenerateTitleAsync("my_podcast_episode.mp3", aiService);

        // Assert
        Assert.Equal("my podcast episode", result);
    }
}

