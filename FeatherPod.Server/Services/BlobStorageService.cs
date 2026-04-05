using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace FeatherPod.Server.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly bool _usesConnectionString;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var azureConfig = config.GetSection("Azure").Get<AzureStorageConfig>()!;
        _containerName = azureConfig.ContainerName;

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
    }

    public async Task InitializeAsync()
    {
        // Create container if it doesn't exist
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        await containerClient.CreateIfNotExistsAsync();

        _logger.LogInformation("Blob storage initialized. Container: {Container}", _containerName);
    }

    public async Task<string?> LoadFeedsConfigAsync()
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("feeds.json");

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("feeds.json");

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(feedsJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved feeds configuration to blob storage");
    }

    public async Task<string?> LoadUsersConfigAsync()
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("users.json");

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("users.json");

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(usersJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved users configuration to blob storage");
    }

    public async Task UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded audio file to blob storage: {FeedId}/{FileName}", feedId, fileName);
    }

    public async Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/pending/{jobId}/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded pending audio file to blob storage: {BlobPath}", blobPath);
    }

    public async Task<Stream> DownloadAudioAsync(string feedId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    }

    public async Task<bool> AudioExistsAsync(string feedId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        return await blobClient.ExistsAsync();
    }

    public async Task DeleteAudioAsync(string feedId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted audio file from blob storage: {FeedId}/{FileName}", feedId, fileName);
    }

    public async Task<List<string>> ListAudioFilesAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var prefix = $"{feedId}/audio/";
        var audioFiles = new List<string>();

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            // Remove the feed prefix and "audio/" from the blob name
            var fileName = blobItem.Name.Substring(prefix.Length);
            audioFiles.Add(fileName);
        }

        return audioFiles;
    }

    public async Task<long> GetAudioFileSizeAsync(string feedId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        var properties = await blobClient.GetPropertiesAsync();
        return properties.Value.ContentLength;
    }

    public async Task<string> DownloadAudioToTempAsync(string feedId, string fileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{fileName}");

        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await blobClient.DownloadToAsync(tempPath);

        return tempPath;
    }

    public async Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);
        var options = new BlobDownloadOptions { Range = new(offset, length) };

        var response = await blobClient.DownloadStreamingAsync(options);

        return response.Value.Content;
    }

    public async Task UploadIconAsync(string feedId, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded icon for feed: {FeedId}", feedId);
    }

    public async Task<string?> GetIconETagAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    }

    public async Task DeleteIconAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted icon for feed: {FeedId}", feedId);
    }

    public async Task SaveEpisodeMetadataAsync(string feedId, string metadataJson)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/episodes.json";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved episode metadata for feed: {FeedId}", feedId);
    }

    public async Task<string?> LoadEpisodeMetadataAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/episodes.json";
        var blobClient = containerClient.GetBlobClient(blobPath);

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient($"{feedId}/push-subscriptions.json");

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient($"{feedId}/push-subscriptions.json");

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(subscriptionsJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved push subscriptions for feed: {FeedId}", feedId);
    }

    public async Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/pending/{jobId}/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        return await blobClient.OpenReadAsync();
    }

    public async Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/pending/{jobId}/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        var builder = new BlobSasBuilder
        {
            BlobContainerName = _containerName,
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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var prefix = $"{feedId}/pending/{jobId}/";

        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            await containerClient.GetBlobClient(blobItem.Name).DeleteIfExistsAsync();
            _logger.LogDebug("Deleted pending blob: {BlobPath}", blobItem.Name);
        }
    }

    public async Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/transcripts/{episodeId}.vtt";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vttContent));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Uploaded transcript to blob storage: {FeedId}/{EpisodeId}", feedId, episodeId);
    }

    public async Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/transcripts/{episodeId}.vtt";
        var blobClient = containerClient.GetBlobClient(blobPath);

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/transcripts/{episodeId}.vtt";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted transcript from blob storage: {FeedId}/{EpisodeId}", feedId, episodeId);
    }

    public async Task RenameFeedAsync(string oldFeedId, string newFeedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var oldPrefix = $"{oldFeedId}/";
        var newPrefix = $"{newFeedId}/";

        // List all blobs with the old prefix
        var blobsToMove = new List<string>();
        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: oldPrefix))
        {
            blobsToMove.Add(blobItem.Name);
        }

        // Copy each blob to new location and delete old one
        foreach (var oldBlobPath in blobsToMove)
        {
            var newBlobPath = string.Concat(newPrefix, oldBlobPath.AsSpan(oldPrefix.Length));

            var sourceBlobClient = containerClient.GetBlobClient(oldBlobPath);
            var destBlobClient = containerClient.GetBlobClient(newBlobPath);

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
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var prefix = $"{feedId}/";

        // List and delete all blobs with this feed prefix
        await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
        {
            var blobClient = containerClient.GetBlobClient(blobItem.Name);
            await blobClient.DeleteAsync();
        }

        _logger.LogInformation("Deleted all blobs for feed: {FeedId}", feedId);
    }
}

// Configuration model for Azure Blob Storage
public record AzureStorageConfig
{
    public string ConnectionString { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "featherpod";
}
