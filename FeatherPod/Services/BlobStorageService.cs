using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Identity;

namespace FeatherPod.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _audioContainerName;
    private readonly string _metadataContainerName;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(IConfiguration config, ILogger<BlobStorageService> logger)
    {
        _logger = logger;

        var azureConfig = config.GetSection("Azure").Get<AzureStorageConfig>()!;
        _audioContainerName = azureConfig.AudioContainerName;
        _metadataContainerName = azureConfig.MetadataContainerName;

        // Create BlobServiceClient
        // Supports both connection string and DefaultAzureCredential (for managed identity)
        if (!string.IsNullOrEmpty(azureConfig.ConnectionString))
        {
            _blobServiceClient = new BlobServiceClient(azureConfig.ConnectionString);
            _logger.LogInformation("Using connection string for blob storage authentication");
        }
        else if (!string.IsNullOrEmpty(azureConfig.AccountName))
        {
            var blobUri = new Uri($"https://{azureConfig.AccountName}.blob.core.windows.net");
            _blobServiceClient = new BlobServiceClient(blobUri, new DefaultAzureCredential());
            _logger.LogInformation("Using managed identity for blob storage authentication");
        }
        else
        {
            throw new InvalidOperationException("Azure storage configuration requires either ConnectionString or AccountName");
        }
    }

    public async Task InitializeAsync()
    {
        // Create containers if they don't exist
        var audioContainer = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        await audioContainer.CreateIfNotExistsAsync(PublicAccessType.None);

        var metadataContainer = _blobServiceClient.GetBlobContainerClient(_metadataContainerName);
        await metadataContainer.CreateIfNotExistsAsync(PublicAccessType.None);

        _logger.LogInformation("Blob storage initialized. Audio container: {Audio}, Metadata container: {Metadata}",
            _audioContainerName, _metadataContainerName);
    }

    // Audio file operations

    public async Task UploadAudioAsync(string fileName, string filePath)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        await using var fileStream = File.OpenRead(filePath);
        await blobClient.UploadAsync(fileStream, overwrite: true);

        _logger.LogInformation("Uploaded audio file to blob storage: {FileName}", fileName);
    }

    public async Task<Stream> DownloadAudioAsync(string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        var response = await blobClient.DownloadStreamingAsync();
        return response.Value.Content;
    }

    public async Task<bool> AudioExistsAsync(string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        return await blobClient.ExistsAsync();
    }

    public async Task DeleteAudioAsync(string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        await blobClient.DeleteIfExistsAsync();
        _logger.LogInformation("Deleted audio file from blob storage: {FileName}", fileName);
    }

    public async Task<List<string>> ListAudioFilesAsync()
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var audioFiles = new List<string>();

        await foreach (var blobItem in containerClient.GetBlobsAsync())
        {
            audioFiles.Add(blobItem.Name);
        }

        return audioFiles;
    }

    public async Task<long> GetAudioFileSizeAsync(string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        var properties = await blobClient.GetPropertiesAsync();
        return properties.Value.ContentLength;
    }

    // Metadata operations

    public async Task SaveMetadataAsync(string metadataJson)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_metadataContainerName);
        var blobClient = containerClient.GetBlobClient("episodes.json");

        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metadataJson));
        await blobClient.UploadAsync(stream, overwrite: true);

        _logger.LogInformation("Saved metadata to blob storage");
    }

    public async Task<string?> LoadMetadataAsync()
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_metadataContainerName);
        var blobClient = containerClient.GetBlobClient("episodes.json");

        if (!await blobClient.ExistsAsync())
        {
            return null;
        }

        var response = await blobClient.DownloadContentAsync();
        return response.Value.Content.ToString();
    }

    // Download audio to temp file for TagLib/Mp4Parser processing
    public async Task<string> DownloadAudioToTempAsync(string fileName)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);

        var containerClient = _blobServiceClient.GetBlobContainerClient(_audioContainerName);
        var blobClient = containerClient.GetBlobClient(fileName);

        await blobClient.DownloadToAsync(tempPath);

        return tempPath;
    }
}

// Configuration model for Azure Blob Storage
public class AzureStorageConfig
{
    public string ConnectionString { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AudioContainerName { get; set; } = "audio";
    public string MetadataContainerName { get; set; } = "metadata";
}
