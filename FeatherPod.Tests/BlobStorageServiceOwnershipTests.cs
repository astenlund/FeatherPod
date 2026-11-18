using Azure.Storage.Blobs;
using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

/// <summary>
/// The blob conditions episode audio ownership relies on, against Azurite: create-only uploads, ETag-conditional deletes, the ETag
/// of a download, and the ETags a feed rename reports.
/// </summary>
[Collection("IntegrationTests")]
public sealed class BlobStorageServiceOwnershipTests : IAsyncLifetime
{
    private const string FeedId = "own-feed";
    private const string FileName = "show.mp3";

    private readonly BlobContainerClient _container = new BlobServiceClient("UseDevelopmentStorage=true").GetBlobContainerClient($"test-ownership-{Guid.NewGuid():N}");
    private readonly string _localDir = Path.Combine(Path.GetTempPath(), $"FeatherPodBlobOwnership_{Guid.NewGuid():N}");
    private BlobStorageService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_localDir);
        _service = new BlobStorageService(new BlobServiceClient("UseDevelopmentStorage=true"), _container, NullLogger<BlobStorageService>.Instance);
        await _service.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DeleteIfExistsAsync();
        Directory.Delete(_localDir, recursive: true);
    }

    [AzuriteFact]
    public async Task CreateAudio_PathTaken_ReturnsNullAndKeepsTheBlob()
    {
        // Arrange
        var first = await _service.CreateAudioAsync(FeedId, FileName, WriteLocal("first", [1, 2, 3]));

        // Act
        var second = await _service.CreateAudioAsync(FeedId, FileName, WriteLocal("second", [9, 9, 9]));

        // Assert
        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal([1, 2, 3], await DownloadAsync());
    }

    [AzuriteFact]
    public async Task DeleteAudio_BlobReplacedSinceItsETag_ReturnsFalseAndKeepsTheBlob()
    {
        // Arrange: the owner's ETag, then another writer replaces the blob
        var owned = await _service.UploadAudioAsync(FeedId, FileName, WriteLocal("owned", [1, 2, 3]));
        await _service.UploadAudioAsync(FeedId, FileName, WriteLocal("replacement", [5, 5, 5]));

        // Act
        var deleted = await _service.DeleteAudioAsync(FeedId, FileName, owned);

        // Assert
        Assert.False(deleted);
        Assert.Equal([5, 5, 5], await DownloadAsync());
    }

    [AzuriteFact]
    public async Task DeleteAudio_MatchingETag_DeletesTheBlob()
    {
        // Arrange
        var owned = await _service.UploadAudioAsync(FeedId, FileName, WriteLocal("owned", [1, 2, 3]));

        // Act
        var deleted = await _service.DeleteAudioAsync(FeedId, FileName, owned);

        // Assert
        Assert.True(deleted);
        Assert.False(await _service.AudioExistsAsync(FeedId, FileName));
    }

    [AzuriteFact]
    public async Task DeleteAudio_MissingBlobWithAnETag_ReturnsTrue()
    {
        // Act
        var deleted = await _service.DeleteAudioAsync(FeedId, FileName, "\"0x8D000000000000\"");

        // Assert
        Assert.True(deleted);
    }

    [AzuriteFact]
    public async Task DownloadAudioToTemp_ReportsTheETagOfTheVersionItHolds()
    {
        // Arrange
        var uploaded = await _service.UploadAudioAsync(FeedId, FileName, WriteLocal("owned", [1, 2, 3]));

        // Act
        var download = await _service.DownloadAudioToTempAsync(FeedId, FileName);

        // Assert
        try
        {
            Assert.Equal(uploaded, download.ETag);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(download.Path));
        }
        finally
        {
            File.Delete(download.Path);
        }
    }

    [AzuriteFact]
    public async Task RenameFeed_ReportsTheMovedAudioBlobsETags()
    {
        // Arrange
        await _service.UploadAudioAsync(FeedId, FileName, WriteLocal("owned", [1, 2, 3]));
        await _service.SaveEpisodeMetadataAsync(FeedId, "[]");

        // Act
        var eTags = await _service.RenameFeedAsync(FeedId, "own-renamed");

        // Assert: only the audio blob is reported, by file name, with the ETag the moved blob now has
        var (fileName, eTag) = Assert.Single(eTags);
        Assert.Equal(FileName, fileName);
        Assert.True(await _service.DeleteAudioAsync("own-renamed", FileName, eTag));
    }

    [AzuriteFact]
    public async Task EpisodeMove_OverRealStorage_MovesAndThenDeletesOwnedAudio()
    {
        // Arrange: a direct upload records the ETag its upload reported; the move compares it with the ETag its download reports
        using var episodes = new EpisodeService(_service, NullLogger<EpisodeService>.Instance);
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Source", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        await episodes.CreateFeedAsync(new FeedConfig { Id = "own-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        var uploaded = await episodes.AddEpisodeAsync(FeedId, WriteLocal(FileName, [1, 2, 3]));

        // Act
        var moved = await episodes.MoveEpisodeAsync(uploaded.Id, FeedId, "own-target");
        await episodes.DeleteEpisodeAsync("own-target", moved.Id);

        // Assert: the move removed the owned source audio, and the delete the moved episode's owned target audio
        Assert.False(await _service.AudioExistsAsync(FeedId, FileName));
        Assert.False(await _service.AudioExistsAsync("own-target", FileName));
    }

    private string WriteLocal(string name, byte[] content)
    {
        var path = Path.Combine(_localDir, name);
        File.WriteAllBytes(path, content);

        return path;
    }

    private async Task<byte[]> DownloadAsync() => (await _container.GetBlobClient(BlobPaths.Audio(FeedId, FileName)).DownloadContentAsync()).Value.Content.ToArray();
}
