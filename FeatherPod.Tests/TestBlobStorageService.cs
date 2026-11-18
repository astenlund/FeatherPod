using FeatherPod.Server.Services;
using FeatherPod.Shared;

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

    public virtual Task<string> UploadAudioAsync(string feedId, string fileName, string filePath)
    {
        var destPath = AudioPath(feedId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Copy(filePath, destPath, overwrite: true);

        return Task.FromResult(MintAudioETag(destPath));
    }

    public virtual Task<string?> CreateAudioAsync(string feedId, string fileName, string filePath)
    {
        var destPath = AudioPath(feedId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        try
        {
            File.Copy(filePath, destPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destPath))
        {
            // Path taken, as a create-only blob upload refuses it
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(MintAudioETag(destPath));
    }

    /// <summary>
    /// The current ETag of an audio file, as blob storage would report it. Every write through this service mints a new one; a file
    /// written directly by a test (simulating the Function's upload) gets a new one the first time it is observed after the write.
    /// </summary>
    public string? GetAudioETag(string feedId, string fileName) => CurrentAudioETag(AudioPath(feedId, fileName));

    private string AudioPath(string feedId, string fileName) => Path.Combine(_rootPath, feedId, "audio", fileName);

    // A minted version stamps its file with a unique sentinel write time far in the past. Any direct write sets the real current time,
    // which no sentinel equals, so a rewrite is a new version even when it lands in the same timestamp tick with the same length
    private static readonly DateTime SentinelEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<string, (string ETag, DateTime Sentinel)> _audioETags = new(StringComparer.OrdinalIgnoreCase);

    // Shared by every instance, so two doubles over the same directory never stamp the same sentinel
    private static long _audioVersions;

    private string MintAudioETag(string path)
    {
        var eTag = $"\"{Guid.NewGuid():N}\"";
        var sentinel = SentinelEpoch.AddTicks(Interlocked.Increment(ref _audioVersions));
        lock (_audioETags)
        {
            File.SetLastWriteTimeUtc(path, sentinel);
            _audioETags[path] = (eTag, sentinel);
        }

        return eTag;
    }

    private string? CurrentAudioETag(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        lock (_audioETags)
        {
            if (_audioETags.TryGetValue(path, out var known) && known.Sentinel == File.GetLastWriteTimeUtc(path))
            {
                return known.ETag;
            }
        }

        return MintAudioETag(path);
    }

    public async Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public virtual Task<bool> DeleteAudioAsync(string feedId, string fileName, string? ifMatchETag = null)
    {
        var filePath = AudioPath(feedId, fileName);
        if (!File.Exists(filePath))
        {
            return Task.FromResult(true);
        }

        if (ifMatchETag != null && CurrentAudioETag(filePath) != ifMatchETag)
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);

        return Task.FromResult(true);
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

    public virtual Task<AudioDownload> DownloadAudioToTempAsync(string feedId, string fileName)
    {
        var sourcePath = AudioPath(feedId, fileName);
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{fileName}");
        var eTag = CurrentAudioETag(sourcePath) ?? throw new FileNotFoundException("Audio blob not found", sourcePath);
        File.Copy(sourcePath, tempPath, overwrite: true);

        return Task.FromResult(new AudioDownload(tempPath, eTag));
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

    public virtual async Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson)
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

    public Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName)
    {
        var path = Path.Combine(_rootPath, feedId, "pending", jobId, fileName);

        return Task.FromResult(new Uri(path).AbsoluteUri);
    }

    public Task<bool> PendingBlobExistsAsync(string feedId, string jobId, string fileName) =>
        Task.FromResult(File.Exists(Path.Combine(_rootPath, feedId, "pending", jobId, fileName)));

    public Task<string> GenerateAudioSasUrlAsync(string feedId, string fileName) =>
        Task.FromResult(new Uri(Path.Combine(_rootPath, feedId, "audio", fileName)).AbsoluteUri);

    public async Task UploadPendingTranscriptAsync(string feedId, string jobId, int attempt, string vttContent)
    {
        var path = GetLocalPath(BlobPaths.PendingTranscript(feedId, jobId, attempt));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, vttContent);
    }

    public virtual async Task<string?> DownloadPendingTranscriptAsync(string feedId, string jobId, int attempt)
    {
        var path = GetLocalPath(BlobPaths.PendingTranscript(feedId, jobId, attempt));

        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    private string GetLocalPath(string blobPath) => Path.Combine([_rootPath, .. blobPath.Split('/')]);

    public async Task DeletePendingJobBlobsAsync(string feedId, string jobId)
    {
        var pendingPath = Path.Combine(_rootPath, feedId, "pending", jobId);
        if (Directory.Exists(pendingPath))
        {
            Directory.Delete(pendingPath, recursive: true);
        }
        await Task.CompletedTask;
    }

    public virtual async Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
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

    public virtual async Task DeleteTranscriptAsync(string feedId, string episodeId)
    {
        var filePath = Path.Combine(_rootPath, feedId, "transcripts", $"{episodeId}.vtt");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        await Task.CompletedTask;
    }

    public virtual Task<IReadOnlyDictionary<string, string>> RenameFeedAsync(string oldFeedId, string newFeedId)
    {
        var oldPath = Path.Combine(_rootPath, oldFeedId);
        var newPath = Path.Combine(_rootPath, newFeedId);
        var audioETags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (Directory.Exists(oldPath))
        {
            Directory.Move(oldPath, newPath);

            // A moved blob is a new blob version, with a new ETag
            var audioDir = Path.Combine(newPath, "audio");
            if (Directory.Exists(audioDir))
            {
                foreach (var file in Directory.GetFiles(audioDir))
                {
                    audioETags[Path.GetFileName(file)] = MintAudioETag(file);
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(audioETags);
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
