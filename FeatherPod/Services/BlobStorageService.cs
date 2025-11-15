using Azure.Storage.Blobs;
using Azure.Identity;

namespace FeatherPod.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
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

    // Feed operations

    public async Task<string?> LoadFeedsConfigAsync()
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("feeds.json");

        if (!await blobClient.ExistsAsync())
        {
            return null;
        }

        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    public async Task SaveFeedsConfigAsync(string feedsJson)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient("feeds.json");

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(feedsJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved feeds configuration to blob storage");
    }

    // Audio file operations (feed-aware)

    public async Task UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded audio file to blob storage: {FeedId}/{FileName}", feedId, fileName);
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
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);

        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/audio/{fileName}";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await blobClient.DownloadToAsync(tempPath);

        return tempPath;
    }

    // Icon operations

    public async Task UploadIconAsync(string feedId, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded icon for feed: {FeedId}", feedId);
    }

    public async Task<bool> IconExistsAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        return await blobClient.ExistsAsync();
    }

    public async Task<Stream> DownloadIconAsync(string feedId)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobPath = $"{feedId}/icon.png";
        var blobClient = containerClient.GetBlobClient(blobPath);

        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    }

    // Episode metadata operations (feed-aware)

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

        if (!await blobClient.ExistsAsync())
        {
            return null;
        }

        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    // Feed management operations

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

            await destBlobClient.StartCopyFromUriAsync(sourceBlobClient.Uri);
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
