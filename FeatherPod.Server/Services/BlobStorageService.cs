using System.Text;

using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

using FeatherPod.Server.Configuration;
using FeatherPod.Shared;

namespace FeatherPod.Server.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _container;
    private readonly bool _usesConnectionString;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var azureConfig = config.GetSection("Azure").Get<AzureStorageConfig>()!;

        // Create BlobServiceClient
        // Supports both connection string and DefaultAzureCredential (for managed identity)
        if (!string.IsNullOrEmpty(azureConfig.ConnectionString))
        {
            _blobServiceClient = new(azureConfig.ConnectionString);
            _usesConnectionString = true;
            _logger.LogInformation("Using connection string for blob storage authentication");
        }
        else if (!string.IsNullOrEmpty(azureConfig.AccountName))
        {
            var blobUri = new Uri($"https://{azureConfig.AccountName}.blob.core.windows.net");
            _blobServiceClient = new(blobUri, new DefaultAzureCredential());
            _logger.LogInformation("Using managed identity for blob storage authentication");
        }
        else
        {
            throw new InvalidOperationException("Azure storage configuration requires either ConnectionString or AccountName");
        }

        _container = _blobServiceClient.GetBlobContainerClient(azureConfig.ContainerName);
    }

    public async Task InitializeAsync()
    {
        // Create container if it doesn't exist
        await _container.CreateIfNotExistsAsync();

        _logger.LogInformation("Blob storage initialized. Container: {Container}", _container.Name);
    }

    public async Task<string?> LoadFeedsConfigAsync()
    {
        var blobClient = _container.GetBlobClient(BlobPaths.FeedsConfig);

        try
        {
            var response = await blobClient.DownloadContentAsync();

            return response.Value.Content.ToString();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveFeedsConfigAsync(string feedsJson)
    {
        await UploadTextAsync(BlobPaths.FeedsConfig, feedsJson);

        _logger.LogInformation("Saved feeds configuration to blob storage");
    }

    public async Task<string?> LoadUsersConfigAsync()
    {
        var blobClient = _container.GetBlobClient(BlobPaths.UsersConfig);

        try
        {
            var response = await blobClient.DownloadContentAsync();

            return response.Value.Content.ToString();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SaveUsersConfigAsync(string usersJson)
    {
        await UploadTextAsync(BlobPaths.UsersConfig, usersJson);

        _logger.LogInformation("Saved users configuration to blob storage");
    }

    public async Task UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded audio file to blob storage: {FeedId}/{FileName}", feedId, fileName);
    }

    public async Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Pending(feedId, jobId, fileName));

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded pending audio file to blob storage: {FeedId}/{JobId}/{FileName}", feedId, jobId, fileName);
    }

    public async Task<Stream> DownloadAudioAsync(string feedId, string fileName)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        var response = await blobClient.DownloadStreamingAsync();

        return response.Value.Content;
    }

    public async Task<bool> AudioExistsAsync(string feedId, string fileName)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        return await blobClient.ExistsAsync();
    }

    public async Task DeleteAudioAsync(string feedId, string fileName)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted audio file from blob storage: {FeedId}/{FileName}", feedId, fileName);
    }

    public async Task<List<string>> ListAudioFilesAsync(string feedId)
    {
        var prefix = BlobPaths.AudioPrefix(feedId);
        var audioFiles = new List<string>();

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix))
        {
            // Remove the feed prefix and "audio/" from the blob name
            var fileName = blobItem.Name[prefix.Length..];
            audioFiles.Add(fileName);
        }

        return audioFiles;
    }

    public async Task<long> GetAudioFileSizeAsync(string feedId, string fileName)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        var properties = await blobClient.GetPropertiesAsync();
        return properties.Value.ContentLength;
    }

    public async Task<string> DownloadAudioToTempAsync(string feedId, string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{fileName}");

        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));

        await blobClient.DownloadToAsync(tempPath);

        return tempPath;
    }

    public async Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Audio(feedId, fileName));
        var options = new BlobDownloadOptions { Range = new(offset, length) };

        var response = await blobClient.DownloadStreamingAsync(options);

        return response.Value.Content;
    }

    public async Task UploadIconAsync(string feedId, string filePath)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Icon(feedId));

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded icon for feed: {FeedId}", feedId);
    }

    public async Task<string?> GetIconETagAsync(string feedId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Icon(feedId));

        try
        {
            var properties = await blobClient.GetPropertiesAsync();
            return properties.Value.ETag.ToString().Trim('"');
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Stream> DownloadIconAsync(string feedId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Icon(feedId));

        var response = await blobClient.DownloadStreamingAsync();

        return response.Value.Content;
    }

    public async Task DeleteIconAsync(string feedId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Icon(feedId));

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted icon for feed: {FeedId}", feedId);
    }

    public async Task SaveEpisodeMetadataAsync(string feedId, string metadataJson)
    {
        await UploadTextAsync(BlobPaths.EpisodesMetadata(feedId), metadataJson);

        _logger.LogInformation("Saved episode metadata for feed: {FeedId}", feedId);
    }

    public async Task<string?> LoadEpisodeMetadataAsync(string feedId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.EpisodesMetadata(feedId));

        try
        {
            var response = await blobClient.DownloadContentAsync();

            return response.Value.Content.ToString();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<string?> LoadPushSubscriptionsAsync(string feedId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.PushSubscriptions(feedId));

        try
        {
            var response = await blobClient.DownloadContentAsync();

            return response.Value.Content.ToString();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson)
    {
        await UploadTextAsync(BlobPaths.PushSubscriptions(feedId), subscriptionsJson);

        _logger.LogInformation("Saved push subscriptions for feed: {FeedId}", feedId);
    }

    public async Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Pending(feedId, jobId, fileName));

        return await blobClient.OpenReadAsync();
    }

    public async Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName)
    {
        var blobPath = BlobPaths.Pending(feedId, jobId, fileName);
        var blobClient = _container.GetBlobClient(blobPath);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
        };
        builder.SetPermissions(BlobSasPermissions.Read);

        if (_usesConnectionString)
        {
            return blobClient.GenerateSasUri(builder).AbsoluteUri;
        }

        var delegationKey = await _blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var accountName = _blobServiceClient.AccountName;
        var sasUri = new BlobUriBuilder(blobClient.Uri) { Sas = builder.ToSasQueryParameters(delegationKey, accountName) };

        return sasUri.ToString();
    }

    public async Task DeletePendingJobBlobsAsync(string feedId, string jobId)
    {
        var prefix = BlobPaths.PendingJobPrefix(feedId, jobId);
        var blobNames = new List<string>();

        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix))
        {
            blobNames.Add(blobItem.Name);
        }

        await Task.WhenAll(blobNames.Select(async name =>
        {
            await _container.GetBlobClient(name).DeleteIfExistsAsync();
            _logger.LogDebug("Deleted pending blob: {BlobPath}", name);
        }));
    }

    public async Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
    {
        await UploadTextAsync(BlobPaths.Transcript(feedId, episodeId), vttContent);

        _logger.LogInformation("Uploaded transcript to blob storage: {FeedId}/{EpisodeId}", feedId, episodeId);
    }

    public async Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Transcript(feedId, episodeId));

        try
        {
            var response = await blobClient.DownloadStreamingAsync();

            return response.Value.Content;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteTranscriptAsync(string feedId, string episodeId)
    {
        var blobClient = _container.GetBlobClient(BlobPaths.Transcript(feedId, episodeId));

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted transcript from blob storage: {FeedId}/{EpisodeId}", feedId, episodeId);
    }

    public async Task RenameFeedAsync(string oldFeedId, string newFeedId)
    {
        var oldPrefix = BlobPaths.FeedPrefix(oldFeedId);
        var newPrefix = BlobPaths.FeedPrefix(newFeedId);

        // List all blobs with the old prefix
        var blobsToMove = new List<string>();
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: oldPrefix))
        {
            blobsToMove.Add(blobItem.Name);
        }

        // Copy each blob to new location and delete old one
        foreach (var oldBlobPath in blobsToMove)
        {
            var newBlobPath = string.Concat(newPrefix, oldBlobPath.AsSpan(oldPrefix.Length));

            var sourceBlobClient = _container.GetBlobClient(oldBlobPath);
            var destBlobClient = _container.GetBlobClient(newBlobPath);

            // Download and re-upload instead of server-side copy, which requires
            // SAS tokens or public access when using Managed Identity authentication
            using var stream = new MemoryStream();
            await sourceBlobClient.DownloadToAsync(stream);
            stream.Position = 0;
            await destBlobClient.UploadAsync(stream, overwrite: true);

            await sourceBlobClient.DeleteAsync();

            _logger.LogInformation("Moved blob: {Old} -> {New}", oldBlobPath, newBlobPath);
        }

        _logger.LogInformation("Renamed feed: {OldId} -> {NewId}", oldFeedId, newFeedId);
    }

    public async Task DeleteFeedAsync(string feedId)
    {
        var prefix = BlobPaths.FeedPrefix(feedId);

        // List and delete all blobs with this feed prefix
        await foreach (var blobItem in _container.GetBlobsAsync(prefix: prefix))
        {
            var blobClient = _container.GetBlobClient(blobItem.Name);
            await blobClient.DeleteAsync();
        }

        _logger.LogInformation("Deleted all blobs for feed: {FeedId}", feedId);
    }

    private async Task UploadTextAsync(string blobPath, string content)
    {
        var blobClient = _container.GetBlobClient(blobPath);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);
    }
}
