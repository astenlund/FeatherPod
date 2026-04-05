using FeatherPod.Server.Services;

namespace FeatherPod.Tests;

/// <summary>
/// Test implementation of blob storage that uses local file system instead of Azure Blob Storage.
/// This allows tests to run without requiring Azure storage or emulator.
/// </summary>
public class TestBlobStorageService : IBlobStorageService
{
    private readonly string _rootPath;
    private readonly string _feedsConfigPath;
    private readonly string _usersConfigPath;

    public TestBlobStorageService(string testDirectory)
    {
        _rootPath = testDirectory;
        _feedsConfigPath = Path.Combine(testDirectory, "feeds.json");
        _usersConfigPath = Path.Combine(testDirectory, "users.json");
        Directory.CreateDirectory(testDirectory);
    }

    public async Task InitializeAsync()
    {
        // Do nothing - we don't need to create actual containers
        await Task.CompletedTask;
    }

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

    public async Task<string?> LoadUsersConfigAsync()
    {
        if (!File.Exists(_usersConfigPath))
        {
            return null;
        }
        return await File.ReadAllTextAsync(_usersConfigPath);
    }

    public async Task SaveUsersConfigAsync(string usersJson)
    {
        await File.WriteAllTextAsync(_usersConfigPath, usersJson);
    }

    public async Task UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var feedAudioPath = Path.Combine(_rootPath, feedId, "audio");
        Directory.CreateDirectory(feedAudioPath);
        var destPath = Path.Combine(feedAudioPath, fileName);
        File.Copy(filePath, destPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath)
    {
        var pendingPath = Path.Combine(_rootPath, feedId, "pending", jobId);
        Directory.CreateDirectory(pendingPath);
        var destPath = Path.Combine(pendingPath, fileName);
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
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        File.Copy(sourcePath, tempPath, overwrite: true);
        return await Task.FromResult(tempPath);
    }

    public async Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length)
    {
        var filePath = Path.Combine(_rootPath, feedId, "audio", fileName);
        var memoryStream = new MemoryStream();

        await using var fileStream = File.OpenRead(filePath);

        fileStream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[81920];
        var remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, toRead));
            if (bytesRead == 0) break;
            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            remaining -= bytesRead;
        }

        memoryStream.Position = 0;

        return memoryStream;
    }

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

    public async Task UploadIconAsync(string feedId, string filePath)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        var feedDir = Path.Combine(_rootPath, feedId);
        Directory.CreateDirectory(feedDir);
        File.Copy(filePath, feedIconPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task<string?> GetIconETagAsync(string feedId)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        if (!File.Exists(feedIconPath))
        {
            return await Task.FromResult<string?>(null);
        }

        var lastWrite = File.GetLastWriteTimeUtc(feedIconPath);
        return await Task.FromResult<string?>(lastWrite.Ticks.ToString("x"));
    }

    public async Task<Stream> DownloadIconAsync(string feedId)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        var fileStream = File.OpenRead(feedIconPath);
        return await Task.FromResult<Stream>(fileStream);
    }

    public async Task DeleteIconAsync(string feedId)
    {
        var feedIconPath = Path.Combine(_rootPath, feedId, "icon.png");
        if (File.Exists(feedIconPath))
        {
            File.Delete(feedIconPath);
        }
        await Task.CompletedTask;
    }

    public async Task<string?> LoadPushSubscriptionsAsync(string feedId)
    {
        var path = Path.Combine(_rootPath, feedId, "push-subscriptions.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path);
    }

    public async Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson)
    {
        var feedDir = Path.Combine(_rootPath, feedId);
        Directory.CreateDirectory(feedDir);
        await File.WriteAllTextAsync(Path.Combine(feedDir, "push-subscriptions.json"), subscriptionsJson);
    }

    public Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName)
    {
        var filePath = Path.Combine(_rootPath, feedId, "pending", jobId, fileName);

        return Task.FromResult<Stream>(File.OpenRead(filePath));
    }

    public async Task DeletePendingJobBlobsAsync(string feedId, string jobId)
    {
        var pendingPath = Path.Combine(_rootPath, feedId, "pending", jobId);
        if (Directory.Exists(pendingPath))
        {
            Directory.Delete(pendingPath, recursive: true);
        }
        await Task.CompletedTask;
    }

    public async Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
    {
        var transcriptDir = Path.Combine(_rootPath, feedId, "transcripts");
        Directory.CreateDirectory(transcriptDir);
        await File.WriteAllTextAsync(Path.Combine(transcriptDir, $"{episodeId}.vtt"), vttContent);
    }

    public async Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId)
    {
        var filePath = Path.Combine(_rootPath, feedId, "transcripts", $"{episodeId}.vtt");
        if (!File.Exists(filePath))
        {
            return await Task.FromResult<Stream?>(null);
        }

        return File.OpenRead(filePath);
    }

    public async Task DeleteTranscriptAsync(string feedId, string episodeId)
    {
        var filePath = Path.Combine(_rootPath, feedId, "transcripts", $"{episodeId}.vtt");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        await Task.CompletedTask;
    }

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
