using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Models;

namespace FeatherPod.Services;

public sealed class EpisodeService : IDisposable
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<EpisodeService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Feed ID → List of Episodes
    private readonly Dictionary<string, List<Episode>> _episodesByFeed = new();
    private FeedsMetadata _feedsMetadata = new();
    private readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    public EpisodeService(IBlobStorageService blobStorage, ILogger<EpisodeService> logger)
    {
        _blobStorage = blobStorage;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await LoadFeedsAsync();
        await LoadAllEpisodesAsync();
        await SyncAllFeedsAsync();
    }

    // Feed management methods

    public async Task<List<FeedConfig>> GetFeedsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _feedsMetadata.Feeds.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FeedConfig?> GetFeedAsync(string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            return _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == feedId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FeedConfig> CreateFeedAsync(FeedConfig feedConfig)
    {
        await _lock.WaitAsync();
        try
        {
            if (_feedsMetadata.Feeds.Any(f => f.Id == feedConfig.Id))
            {
                throw new InvalidOperationException($"Feed with ID '{feedConfig.Id}' already exists");
            }

            _feedsMetadata = _feedsMetadata with
            {
                Feeds = _feedsMetadata.Feeds.Append(feedConfig).ToList()
            };

            _episodesByFeed[feedConfig.Id] = new List<Episode>();

            await SaveFeedsAsync();
            _logger.LogInformation("Created feed: {FeedId}", feedConfig.Id);

            return feedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<FeedConfig> UpdateFeedAsync(string feedId, FeedConfig updatedConfig)
    {
        await _lock.WaitAsync();
        try
        {
            var existingIndex = _feedsMetadata.Feeds.FindIndex(f => f.Id == feedId);
            if (existingIndex == -1)
            {
                throw new InvalidOperationException($"Feed '{feedId}' not found");
            }

            // Ensure ID doesn't change
            if (updatedConfig.Id != feedId)
            {
                throw new InvalidOperationException("Cannot change feed ID via update. Use rename instead.");
            }

            var feeds = _feedsMetadata.Feeds.ToList();
            feeds[existingIndex] = updatedConfig;
            _feedsMetadata = new FeedsMetadata { Feeds = feeds };

            await SaveFeedsAsync();
            _logger.LogInformation("Updated feed: {FeedId}", feedId);

            return updatedConfig;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RenameFeedAsync(string oldFeedId, string newFeedId)
    {
        await _lock.WaitAsync();
        try
        {
            var feed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == oldFeedId);
            if (feed == null)
            {
                throw new InvalidOperationException($"Feed '{oldFeedId}' not found");
            }

            if (_feedsMetadata.Feeds.Any(f => f.Id == newFeedId))
            {
                throw new InvalidOperationException($"Feed with ID '{newFeedId}' already exists");
            }

            // Rename in blob storage first
            await _blobStorage.RenameFeedAsync(oldFeedId, newFeedId);

            // Update feed config
            var updatedFeed = feed with { Id = newFeedId };
            var feeds = _feedsMetadata.Feeds.Where(f => f.Id != oldFeedId).Append(updatedFeed).ToList();
            _feedsMetadata = new FeedsMetadata { Feeds = feeds };

            // Update episodes in memory
            if (_episodesByFeed.TryGetValue(oldFeedId, out var episodes))
            {
                // Update each episode's FeedId
                var updatedEpisodes = episodes.Select(e => e with { FeedId = newFeedId }).ToList();
                _episodesByFeed.Remove(oldFeedId);
                _episodesByFeed[newFeedId] = updatedEpisodes;
            }

            await SaveFeedsAsync();
            _logger.LogInformation("Renamed feed: {OldId} → {NewId}", oldFeedId, newFeedId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteFeedAsync(string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            var feed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == feedId);
            if (feed == null)
            {
                throw new InvalidOperationException($"Feed '{feedId}' not found");
            }

            // Delete from blob storage
            await _blobStorage.DeleteFeedAsync(feedId);

            // Remove from feeds list
            _feedsMetadata = _feedsMetadata with
            {
                Feeds = _feedsMetadata.Feeds.Where(f => f.Id != feedId).ToList()
            };

            // Remove episodes from memory
            _episodesByFeed.Remove(feedId);

            await SaveFeedsAsync();
            _logger.LogInformation("Deleted feed: {FeedId}", feedId);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Episode methods

    public async Task<List<Episode>> GetAllEpisodesAsync(string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            return !_episodesByFeed.TryGetValue(feedId, out var value)
                ? []
                : value.OrderByDescending(e => e.PublishedDate).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode?> GetEpisodeByIdAsync(string feedId, string id)
    {
        await _lock.WaitAsync();
        try
        {
            return !_episodesByFeed.TryGetValue(feedId, out var value)
                ? null
                : value.FirstOrDefault(e => e.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode> AddEpisodeAsync(
        string feedId,
        string filePath,
        string? title = null,
        string? description = null,
        DateTime? publishedDate = null,
        bool? useMetadataForPublishedDate = null)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Audio file not found", filePath);

        // Verify feed exists
        var feed = await GetFeedAsync(feedId);
        if (feed == null)
        {
            throw new InvalidOperationException($"Feed '{feedId}' not found");
        }

        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;
        var id = Episode.GenerateId(feedId, fileName, fileSize);

        await _lock.WaitAsync();
        try
        {
            // Initialize episode list for feed if doesn't exist
            if (!_episodesByFeed.ContainsKey(feedId))
            {
                _episodesByFeed[feedId] = [];
            }

            // Check if episode already exists
            var existing = _episodesByFeed[feedId].FirstOrDefault(e => e.Id == id);
            if (existing != null)
            {
                _logger.LogInformation("Episode with ID {Id} already exists in feed {FeedId}, skipping", id, feedId);
                return existing;
            }

            var duration = await GetAudioDurationAsync(filePath);

            // Determine published date
            DateTime finalPublishedDate;
            if (publishedDate.HasValue)
            {
                finalPublishedDate = publishedDate.Value;
                _logger.LogDebug("Using explicitly provided published date for {File}: {Date}", fileName, finalPublishedDate);
            }
            else if (useMetadataForPublishedDate.HasValue && useMetadataForPublishedDate.Value)
            {
                finalPublishedDate = GetPublishedDate(filePath);
                _logger.LogDebug("Using file metadata (per-request) for published date for {File}: {Date}", fileName, finalPublishedDate);
            }
            else if (feed.UseFileMetadataForPublishDate)
            {
                finalPublishedDate = GetPublishedDate(filePath);
                _logger.LogDebug("Using file metadata (config) for published date for {File}: {Date}", fileName, finalPublishedDate);
            }
            else
            {
                finalPublishedDate = DateTime.UtcNow;
                _logger.LogDebug("Using current time for published date for {File}: {Date}", fileName, finalPublishedDate);
            }

            // Upload audio file to blob storage
            await _blobStorage.UploadAudioAsync(feedId, fileName, filePath);

            // Create episode
            var episode = new Episode
            {
                Id = id,
                FeedId = feedId,
                Title = title ?? ParseTitleFromFilename(fileName),
                Description = description,
                FileName = fileName,
                FileSize = fileSize,
                Duration = duration,
                PublishedDate = finalPublishedDate
            };

            _episodesByFeed[feedId].Add(episode);
            await SaveEpisodesAsync(feedId);

            _logger.LogInformation("Added episode to feed {FeedId}: {Title} ({FileName})", feedId, episode.Title, fileName);
            return episode;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteEpisodeAsync(string feedId, string id)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var value))
            {
                return false;
            }

            var episode = value.FirstOrDefault(e => e.Id == id);
            if (episode == null)
            {
                return false;
            }

            // Delete from blob storage
            await _blobStorage.DeleteAudioAsync(feedId, episode.FileName);

            // Remove from list
            _episodesByFeed[feedId].Remove(episode);
            await SaveEpisodesAsync(feedId);

            _logger.LogInformation("Deleted episode from feed {FeedId}: {Title}", feedId, episode.Title);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode> MoveEpisodeAsync(string episodeId, string sourceFeedId, string targetFeedId)
    {
        await _lock.WaitAsync();
        try
        {
            // Verify both feeds exist
            var sourceFeed = await GetFeedAsync(sourceFeedId);
            var targetFeed = await GetFeedAsync(targetFeedId);
            if (sourceFeed == null || targetFeed == null)
            {
                throw new InvalidOperationException("Source or target feed not found");
            }

            // Find episode in source feed
            var episode = _episodesByFeed[sourceFeedId].FirstOrDefault(e => e.Id == episodeId);
            if (episode == null)
            {
                throw new InvalidOperationException($"Episode '{episodeId}' not found in feed '{sourceFeedId}'");
            }

            // Move blob in storage
            var tempPath = await _blobStorage.DownloadAudioToTempAsync(sourceFeedId, episode.FileName);
            await _blobStorage.UploadAudioAsync(targetFeedId, episode.FileName, tempPath);
            await _blobStorage.DeleteAudioAsync(sourceFeedId, episode.FileName);
            File.Delete(tempPath);

            // Update episode and move to target feed
            var newId = Episode.GenerateId(targetFeedId, episode.FileName, episode.FileSize);
            var movedEpisode = episode with
            {
                Id = newId,
                FeedId = targetFeedId
            };

            _episodesByFeed[sourceFeedId].Remove(episode);
            if (!_episodesByFeed.ContainsKey(targetFeedId))
            {
                _episodesByFeed[targetFeedId] = [];
            }
            _episodesByFeed[targetFeedId].Add(movedEpisode);

            await SaveEpisodesAsync(sourceFeedId);
            await SaveEpisodesAsync(targetFeedId);

            _logger.LogInformation("Moved episode {Id} from feed {Source} to {Target}", episodeId, sourceFeedId, targetFeedId);
            return movedEpisode;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode> CopyEpisodeAsync(string episodeId, string sourceFeedId, string targetFeedId)
    {
        await _lock.WaitAsync();
        try
        {
            // Verify both feeds exist
            var sourceFeed = await GetFeedAsync(sourceFeedId);
            var targetFeed = await GetFeedAsync(targetFeedId);
            if (sourceFeed == null || targetFeed == null)
            {
                throw new InvalidOperationException("Source or target feed not found");
            }

            // Find episode in source feed
            var episode = _episodesByFeed[sourceFeedId].FirstOrDefault(e => e.Id == episodeId);
            if (episode == null)
            {
                throw new InvalidOperationException($"Episode '{episodeId}' not found in feed '{sourceFeedId}'");
            }

            // Copy blob in storage
            var tempPath = await _blobStorage.DownloadAudioToTempAsync(sourceFeedId, episode.FileName);
            await _blobStorage.UploadAudioAsync(targetFeedId, episode.FileName, tempPath);
            File.Delete(tempPath);

            // Create copied episode with new ID
            var newId = Episode.GenerateId(targetFeedId, episode.FileName, episode.FileSize);
            var copiedEpisode = episode with
            {
                Id = newId,
                FeedId = targetFeedId
            };

            if (!_episodesByFeed.ContainsKey(targetFeedId))
            {
                _episodesByFeed[targetFeedId] = new List<Episode>();
            }
            _episodesByFeed[targetFeedId].Add(copiedEpisode);

            await SaveEpisodesAsync(targetFeedId);

            _logger.LogInformation("Copied episode {Id} from feed {Source} to {Target}", episodeId, sourceFeedId, targetFeedId);
            return copiedEpisode;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SyncWithBlobStorageAsync(string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_episodesByFeed.ContainsKey(feedId))
            {
                _episodesByFeed[feedId] = [];
            }

            var blobFiles = await _blobStorage.ListAudioFilesAsync(feedId);
            var metadataFiles = _episodesByFeed[feedId].Select(e => e.FileName).ToHashSet();

            // Remove episodes whose blob files are missing
            var episodesToRemove = _episodesByFeed[feedId]
                .Where(e => !blobFiles.Contains(e.FileName))
                .ToList();

            foreach (var episode in episodesToRemove)
            {
                _episodesByFeed[feedId].Remove(episode);
                _logger.LogInformation("Removed episode with missing blob file from feed {FeedId}: {FileName}", feedId, episode.FileName);
            }

            if (episodesToRemove.Any())
            {
                await SaveEpisodesAsync(feedId);
            }

            _logger.LogInformation("Sync complete for feed {FeedId}. Removed {Count} episodes with missing files.", feedId, episodesToRemove.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Private helper methods

    private async Task LoadFeedsAsync()
    {
        var feedsJson = await _blobStorage.LoadFeedsConfigAsync();
        if (feedsJson != null)
        {
            _feedsMetadata = JsonSerializer.Deserialize<FeedsMetadata>(feedsJson) ?? new FeedsMetadata();
            _logger.LogInformation("Loaded {Count} feeds from blob storage", _feedsMetadata.Feeds.Count);
        }
        else
        {
            _logger.LogInformation("No feeds configuration found in blob storage, starting with empty list");
        }
    }

    private async Task SaveFeedsAsync()
    {
        var json = JsonSerializer.Serialize(_feedsMetadata, _jsonSerializerOptions);
        await _blobStorage.SaveFeedsConfigAsync(json);
    }

    private async Task LoadAllEpisodesAsync()
    {
        foreach (var feed in _feedsMetadata.Feeds)
        {
            var metadataJson = await _blobStorage.LoadEpisodeMetadataAsync(feed.Id);
            if (metadataJson != null)
            {
                var episodes = JsonSerializer.Deserialize<List<Episode>>(metadataJson) ?? [];
                _episodesByFeed[feed.Id] = episodes;
                _logger.LogInformation("Loaded {Count} episodes for feed {FeedId}", episodes.Count, feed.Id);
            }
            else
            {
                _episodesByFeed[feed.Id] = [];
            }
        }
    }

    private async Task SaveEpisodesAsync(string feedId)
    {
        if (!_episodesByFeed.ContainsKey(feedId))
        {
            return;
        }

        var json = JsonSerializer.Serialize(_episodesByFeed[feedId], _jsonSerializerOptions);
        await _blobStorage.SaveEpisodeMetadataAsync(feedId, json);
    }

    private async Task SyncAllFeedsAsync()
    {
        foreach (var feed in _feedsMetadata.Feeds)
        {
            await SyncWithBlobStorageAsync(feed.Id);
        }
    }

    private Task<TimeSpan> GetAudioDurationAsync(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return Task.FromResult(file.Properties.Duration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read audio duration from {FilePath}, using 0:00", filePath);
            return Task.FromResult(TimeSpan.Zero);
        }
    }

    private DateTime GetPublishedDate(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);

            // Try DateTagged first (user-editable metadata)
            if (file.Tag.DateTagged.HasValue)
            {
                _logger.LogInformation("Using DateTagged from audio metadata: {Date}", file.Tag.DateTagged.Value);
                return file.Tag.DateTagged.Value.ToUniversalTime();
            }

            // For M4A/MP4 files, try to read container creation time
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension == ".m4a" || extension == ".mp4")
            {
                var creationTime = Mp4Parser.GetCreationTime(filePath);
                if (creationTime.HasValue)
                {
                    _logger.LogInformation("Using MP4 container creation time: {Date}", creationTime.Value);
                    return creationTime.Value;
                }
            }

            _logger.LogDebug("No metadata date found for {FilePath}, using current time", filePath);
            return DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read published date from {FilePath}, using current time", filePath);
            return DateTime.UtcNow;
        }
    }

    private static string ParseTitleFromFilename(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var title = nameWithoutExtension.Replace('_', ' ');

        // Handle PascalCase: Insert space before uppercase letters that follow lowercase letters
        // But preserve sequences like "2D", "3D", "4K" (digit followed by uppercase)
        title = Regex.Replace(title, "(?<![A-Z0-9])(?=[A-Z])", " ");

        return title.Trim();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
