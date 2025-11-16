using FeatherPod.Services;

namespace FeatherPod.Tests;

/// <summary>
/// Test implementation of blob storage that uses local file system instead of Azure Blob Storage.
/// This allows tests to run without requiring Azure storage or emulator.
/// </summary>
public class TestBlobStorageService : IBlobStorageService
{
    private readonly string _rootPath;
    private readonly string _feedsConfigPath;

    public TestBlobStorageService(string testDirectory)
    {
        _rootPath = testDirectory;
        _feedsConfigPath = Path.Combine(testDirectory, "feeds.json");
        Directory.CreateDirectory(testDirectory);
    }

    public async Task InitializeAsync()
    {
        // Do nothing - we don't need to create actual containers
        await Task.CompletedTask;
    }

    // Feed configuration
    public async Task<string?> LoadFeedsConfigAsync()
    {
        if (!File.Exists(_feedsConfigPath))
        {
            return null;
        }
        return await File.ReadAllTextAsync(_feedsConfigPath);
    }

    public async Task SaveFeedsConfigAsync(string feedsJson)
    {
        await File.WriteAllTextAsync(_feedsConfigPath, feedsJson);
    }

    // Audio operations (feed-aware)
    public async Task UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var feedAudioPath = Path.Combine(_rootPath, feedId, "audio");
        Directory.CreateDirectory(feedAudioPath);
        var destPath = Path.Combine(feedAudioPath, fileName);
        File.Copy(filePath, destPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task<Stream> DownloadAudioAsync(string feedId, string fileName)
    {
        var filePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        var fileStream = File.OpenRead(filePath);
        return await Task.FromResult<Stream>(fileStream);
    }

    public async Task<bool> AudioExistsAsync(string feedId, string fileName)
    {
        var filePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        return await Task.FromResult(File.Exists(filePath));
    }

    public async Task DeleteAudioAsync(string feedId, string fileName)
    {
        var filePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        await Task.CompletedTask;
    }

    public async Task<List<string>> ListAudioFilesAsync(string feedId)
    {
        var feedAudioPath = Path.Combine(_rootPath, feedId, "audio");
        if (!Directory.Exists(feedAudioPath))
        {
            return new List<string>();
        }

        var files = Directory.GetFiles(feedAudioPath)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToList();

        return await Task.FromResult(files);
    }

    public async Task<long> GetAudioFileSizeAsync(string feedId, string fileName)
    {
        var filePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        var fileInfo = new FileInfo(filePath);
        return await Task.FromResult(fileInfo.Length);
    }

    public async Task<string> DownloadAudioToTempAsync(string feedId, string fileName)
    {
        var sourcePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        File.Copy(sourcePath, tempPath, overwrite: true);
        return await Task.FromResult(tempPath);
    }

    // Episode metadata operations (feed-aware)
    public async Task SaveEpisodeMetadataAsync(string feedId, string metadataJson)
    {
        var feedMetadataPath = Path.Combine(_rootPath, feedId, "episodes.json");
        var feedDir = Path.Combine(_rootPath, feedId);
        Directory.CreateDirectory(feedDir);
        await File.WriteAllTextAsync(feedMetadataPath, metadataJson);
    }

    public async Task<string?> LoadEpisodeMetadataAsync(string feedId)
    {
        var feedMetadataPath = Path.Combine(_rootPath, feedId, "episodes.json");
        if (!File.Exists(feedMetadataPath))
        {
            return null;
        }
        return await File.ReadAllTextAsync(feedMetadataPath);
    }

    // Icon operations (feed-aware)
    public async Task UploadIconAsync(string feedId, string filePath)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        var feedDir = Path.Combine(_rootPath, feedId);
        Directory.CreateDirectory(feedDir);
        File.Copy(filePath, feedIconPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task<bool> IconExistsAsync(string feedId)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        return await Task.FromResult(File.Exists(feedIconPath));
    }

    public async Task<Stream> DownloadIconAsync(string feedId)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        var fileStream = File.OpenRead(feedIconPath);
        return await Task.FromResult<Stream>(fileStream);
    }

    // Feed management operations
    public async Task RenameFeedAsync(string oldFeedId, string newFeedId)
    {
        var oldPath = Path.Combine(_rootPath, oldFeedId);
        var newPath = Path.Combine(_rootPath, newFeedId);

        if (Directory.Exists(oldPath))
        {
            Directory.Move(oldPath, newPath);
        }

        await Task.CompletedTask;
    }

    public async Task DeleteFeedAsync(string feedId)
    {
        var feedPath = Path.Combine(_rootPath, feedId);

        if (Directory.Exists(feedPath))
        {
            Directory.Delete(feedPath, recursive: true);
        }

        await Task.CompletedTask;
    }
}
