using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FeatherPod.Models;
using FeatherPod.Services;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class EpisodeServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _episodesPath;
    private readonly ILogger<EpisodeService> _logger;
    private readonly IConfiguration _configuration;

    private readonly List<EpisodeService> _servicesToDispose = new();
    private readonly List<TestBlobStorageService> _blobServicesToDispose = new();

    public EpisodeServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        _episodesPath = Path.Combine(_testDirectory, "audio");

        Directory.CreateDirectory(_testDirectory);

        // Create logger that suppresses warnings from test dummy files
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Error); // Only show errors and above
        });
        _logger = loggerFactory.CreateLogger<EpisodeService>();

        // Create test configuration
        var configData = new Dictionary<string, string>
        {
            ["Podcast:UseFileMetadataForPublishDate"] = "false"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
    }

    private EpisodeService CreateService()
    {
        var blobStorage = new TestBlobStorageService(_testDirectory);
        _blobServicesToDispose.Add(blobStorage);

        var service = new EpisodeService(blobStorage, _configuration, _logger);
        _servicesToDispose.Add(service);
        return service;
    }

    private EpisodeService CreateServiceWithFileMetadata(bool useFileMetadata)
    {
        var configData = new Dictionary<string, string>
        {
            ["Podcast:UseFileMetadataForPublishDate"] = useFileMetadata.ToString().ToLower()
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();

        var blobStorage = new TestBlobStorageService(_testDirectory);
        _blobServicesToDispose.Add(blobStorage);

        var service = new EpisodeService(blobStorage, configuration, _logger);
        _servicesToDispose.Add(service);
        return service;
    }

    [Fact]
    public async Task InitializeAsync_ShouldLoadExistingMetadata()
    {
        // Arrange
        var testEpisode = new Episode
        {
            Id = "test123",
            Title = "Test Episode",
            FileName = "test.mp3",
            FileSize = 1000
        };

        var json = System.Text.Json.JsonSerializer.Serialize(new[] { testEpisode }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var metadataPath = Path.Combine(_testDirectory, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, json);

        // Create the actual file in the audio directory
        Directory.CreateDirectory(_episodesPath);
        await File.WriteAllTextAsync(Path.Combine(_episodesPath, "test.mp3"), "fake audio data");

        var service = CreateService();

        // Act
        await service.InitializeAsync();
        var episodes = await service.GetAllEpisodesAsync();

        // Assert
        Assert.Single(episodes);
        Assert.Equal("Test Episode", episodes[0].Title);
    }

    [Fact]
    public async Task GetAllEpisodesAsync_ShouldReturnEpisodesOrderedByPublishedDate()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        // Create test files
        var file1 = Path.Combine(_testDirectory, "test1.mp3");
        var file2 = Path.Combine(_testDirectory, "test2.mp3");
        await File.WriteAllTextAsync(file1, "audio1");
        await File.WriteAllTextAsync(file2, "audio2");

        // Act
        await service.AddEpisodeAsync(file1, "Episode 1");
        await Task.Delay(10); // Ensure different timestamps
        await service.AddEpisodeAsync(file2, "Episode 2");

        var episodes = await service.GetAllEpisodesAsync();

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

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");
        var fileSize = new FileInfo(testFile).Length;
        var expectedId = Episode.GenerateId("test.mp3", fileSize);

        // Act
        var episode = await service.AddEpisodeAsync(testFile, "Test");

        // Assert
        Assert.Equal(expectedId, episode.Id);
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldCopyFileToEpisodesFolder()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        await service.AddEpisodeAsync(testFile, "Test");

        // Assert
        var copiedFile = Path.Combine(_episodesPath, "test.mp3");
        Assert.True(File.Exists(copiedFile));
    }

    [Fact]
    public async Task AddEpisodeAsync_ShouldNotAddDuplicate_WhenSameFileAlreadyExists()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        // Act
        await service.AddEpisodeAsync(testFile, "Test 1");
        await service.AddEpisodeAsync(testFile, "Test 2"); // Try to add again

        var episodes = await service.GetAllEpisodesAsync();

        // Assert
        Assert.Single(episodes);
        Assert.Equal("Test 1", episodes[0].Title); // Original title preserved
    }

    [Fact]
    public async Task DeleteEpisodeAsync_ShouldRemoveEpisodeAndFile()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var episode = await service.AddEpisodeAsync(testFile, "Test");

        // Act
        var result = await service.DeleteEpisodeAsync(episode.Id);

        // Assert
        Assert.True(result);
        var episodes = await service.GetAllEpisodesAsync();
        Assert.Empty(episodes);

        var audioFile = Path.Combine(_episodesPath, "test.mp3");
        Assert.False(File.Exists(audioFile));
    }

    [Fact]
    public async Task DeleteEpisodeAsync_ShouldReturnFalse_WhenEpisodeNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        // Act
        var result = await service.DeleteEpisodeAsync("nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SyncWithBlobStorageAsync_ShouldRemoveEpisodesWithMissingFiles()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        _ = await service.AddEpisodeAsync(testFile, "Test");

        // Delete the audio file manually from blob storage
        var audioFile = Path.Combine(_episodesPath, "test.mp3");
        File.Delete(audioFile);

        // Act
        await service.SyncWithBlobStorageAsync();

        var episodes = await service.GetAllEpisodesAsync();

        // Assert
        Assert.Empty(episodes);
    }

    [Fact]
    public async Task GetEpisodeByIdAsync_ShouldReturnCorrectEpisode()
    {
        // Arrange
        var service = CreateService();
        await service.InitializeAsync();

        var testFile = Path.Combine(_testDirectory, "test.mp3");
        await File.WriteAllTextAsync(testFile, "audio data");

        var addedEpisode = await service.AddEpisodeAsync(testFile, "Test");

        // Act
        var episode = await service.GetEpisodeByIdAsync(addedEpisode.Id);

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

        // Act
        var episode = await service.GetEpisodeByIdAsync("nonexistent");

        // Assert
        Assert.Null(episode);
    }

    [Fact]
    public void ParseTitleFromFilename_ShouldReplaceUnderscoresWithSpaces()
    {
        // Act
        var result = EpisodeService.ParseTitleFromFilename("2000_FPS_Image_Rendering__How_GaussianImage_Broke_the_Speed_Bar.m4a");

        // Assert
        Assert.Equal("2000 FPS Image Rendering How Gaussian Image Broke the Speed Bar", result);
    }

    [Fact]
    public void ParseTitleFromFilename_ShouldHandlePascalCase()
    {
        // Act
        var result = EpisodeService.ParseTitleFromFilename("MyPodcastEpisode.mp3");

        // Assert
        Assert.Equal("My Podcast Episode", result);
    }

    [Fact]
    public void ParseTitleFromFilename_ShouldHandleMixedFormats()
    {
        // Act
        var result = EpisodeService.ParseTitleFromFilename("Episode_01_TheBeginning.mp3");

        // Assert
        Assert.Equal("Episode 01 The Beginning", result);
    }

    [Fact]
    public void ParseTitleFromFilename_ShouldKeepDigitLetterCombinations()
    {
        // Act
        var result = EpisodeService.ParseTitleFromFilename("Introduction_to_2D_Graphics.mp3");

        // Assert - "2D" should remain as "2D", not become "2 D"
        Assert.Equal("Introduction to 2D Graphics", result);
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
