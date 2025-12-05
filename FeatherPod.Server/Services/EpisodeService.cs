using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

public sealed partial class EpisodeService : IDisposable
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
            _feedsMetadata = new() { Feeds = feeds };

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
            _feedsMetadata = new() { Feeds = feeds };

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

    public async Task<List<Episode>> GetRecentUploadsAsync(string feedId, UploadSource? source, int limit)
    {
        await _lock.WaitAsync();

        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                return [];
            }

            var query = episodes.AsEnumerable();

            if (source.HasValue)
            {
                query = query.Where(e => e.Source == source.Value);
            }

            return query
                .OrderByDescending(e => e.UploadedAt)
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Check data integrity - verifies episode metadata can be loaded and audio blobs exist.
    /// </summary>
    /// <param name="feedIds">Optional list of feed IDs to check. If null or empty, checks all feeds.</param>
    public async Task<DataIntegrityReport> CheckDataIntegrityAsync(IEnumerable<string>? feedIds = null)
    {
        List<(string FeedId, Episode Episode)> episodesToCheck;

        await _lock.WaitAsync();

        try
        {
            var feedIdSet = feedIds?.ToHashSet();
            episodesToCheck = _episodesByFeed
                .Where(kvp => feedIdSet == null || feedIdSet.Count == 0 || feedIdSet.Contains(kvp.Key))
                .SelectMany(kvp => kvp.Value.Select(e => (kvp.Key, e)))
                .ToList();
        }
        finally
        {
            _lock.Release();
        }

        var report = new DataIntegrityReport { TotalEpisodes = episodesToCheck.Count };

        foreach (var (feedId, episode) in episodesToCheck)
        {
            var audioExists = await _blobStorage.AudioExistsAsync(feedId, episode.FileName);
            if (audioExists)
            {
                report.ValidEpisodes++;
            }
            else
            {
                report.MissingBlobs.Add(new(feedId, episode.Id, episode.FileName, episode.Title));
            }
        }

        return report;
    }

    public record DataIntegrityReport
    {
        public int TotalEpisodes { get; init; }
        public int ValidEpisodes { get; set; }
        public List<EpisodeReference> MissingBlobs { get; init; } = [];
    }

    public record EpisodeReference(string FeedId, string EpisodeId, string FileName, string Title);

    public async Task<Episode> AddEpisodeAsync(
        string feedId,
        string filePath,
        string? title = null,
        string? description = null,
        string? summary = null,
        DateTime? publishedDate = null,
        string? episodeId = null,
        UploadSource source = UploadSource.CLI,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        var feed = await GetFeedAsync(feedId);
        if (feed == null)
        {
            throw new InvalidOperationException($"Feed '{feedId}' not found");
        }

        var fileName = fileInfo.Name;
        var id = episodeId ?? Episode.GenerateId(feedId, fileName, fileInfo.Length);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                episodes = [];
                _episodesByFeed[feedId] = episodes;
            }

            var existingEpisode = episodes.FirstOrDefault(e => e.Id == id);
            if (existingEpisode != null)
            {
                _logger.LogInformation("Episode with ID {Id} already exists in feed {FeedId}, replacing with new upload", id, feedId);
                episodes.Remove(existingEpisode);
            }

            var duration = GetAudioDuration(filePath);

            DateTime finalPublishedDate;
            if (publishedDate.HasValue)
            {
                finalPublishedDate = publishedDate.Value;
                _logger.LogDebug("Using explicitly provided published date for {File}: {Date}", fileName, finalPublishedDate);
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

            await _blobStorage.UploadAudioAsync(feedId, fileName, filePath);

            var episode = new Episode
            {
                Id = id,
                FeedId = feedId,
                Title = title ?? ParseTitleFromFilename(fileName),
                Description = description,
                Summary = summary,
                FileName = fileName,
                FileSize = fileInfo.Length,
                Duration = duration,
                PublishedDate = finalPublishedDate,
                Source = source,
                UploadedAt = DateTime.UtcNow
            };

            episodes.Add(episode);
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

            // Only delete blob if no other episodes reference the same file
            var otherEpisodeSharesFile = value.Any(e => e.Id != id && e.FileName == episode.FileName);
            if (!otherEpisodeSharesFile)
            {
                await _blobStorage.DeleteAudioAsync(feedId, episode.FileName);
            }
            else
            {
                _logger.LogInformation("Skipping blob deletion - another episode references {FileName}", episode.FileName);
            }

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
            var sourceFeed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == sourceFeedId);
            var targetFeed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == targetFeedId);
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
            var sourceFeed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == sourceFeedId);
            var targetFeed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == targetFeedId);
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
            // Reload episodes from blob storage (picks up changes from Azure Functions)
            var metadataJson = await _blobStorage.LoadEpisodeMetadataAsync(feedId);
            if (metadataJson != null)
            {
                var episodes = JsonSerializer.Deserialize<List<Episode>>(metadataJson) ?? [];
                _episodesByFeed[feedId] = episodes;
                _logger.LogInformation("Reloaded {Count} episodes for feed {FeedId} from blob storage", episodes.Count, feedId);
            }
            else
            {
                _episodesByFeed[feedId] = [];
            }

            var blobFiles = await _blobStorage.ListAudioFilesAsync(feedId);

            // Warn about episodes whose blob files are missing (don't auto-delete to prevent silent data loss)
            var orphanedEpisodes = _episodesByFeed[feedId]
                .Where(e => !blobFiles.Contains(e.FileName))
                .ToList();

            foreach (var episode in orphanedEpisodes)
            {
                _logger.LogWarning("Episode has missing blob file in feed {FeedId}: {Title} ({FileName})", feedId, episode.Title, episode.FileName);
            }

            _logger.LogInformation("Sync complete for feed {FeedId}. Found {Count} episodes with missing files.", feedId, orphanedEpisodes.Count);
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
        if (!_episodesByFeed.TryGetValue(feedId, out var value))
        {
            return;
        }

        var json = JsonSerializer.Serialize(value, _jsonSerializerOptions);

        await _blobStorage.SaveEpisodeMetadataAsync(feedId, json);
    }

    private async Task SyncAllFeedsAsync()
    {
        foreach (var feed in _feedsMetadata.Feeds)
        {
            await SyncWithBlobStorageAsync(feed.Id);
        }
    }

    private TimeSpan GetAudioDuration(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);

            return file.Properties.Duration;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read audio duration from {FilePath}, using 0:00", filePath);

            return TimeSpan.Zero;
        }
    }

    public static bool TryGetPublishedDateFromFile(string filePath, [NotNullWhen(true)] out DateTime? date)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);

            // Try DateTagged first (user-editable metadata)
            if (file.Tag.DateTagged.HasValue)
            {
                date = file.Tag.DateTagged.Value.ToUniversalTime();

                return true;
            }

            // For M4A/MP4 files, try to read container creation time
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (extension == ".m4a" || extension == ".mp4")
            {
                var creationTime = Mp4Parser.GetCreationTime(filePath);
                if (creationTime.HasValue)
                {
                    date = creationTime.Value;

                    return true;
                }
            }

            date = null;

            return false;
        }
        catch
        {
            date = null;

            return false;
        }
    }

    public static string ParseTitleFromFilename(string fileName)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        // URL-decode any percent-encoded characters (e.g., %E2%80%99 → ', %2C → ,)
        var title = Uri.UnescapeDataString(nameWithoutExtension);

        // Normalize curly quotes to straight quotes
        title = title.Replace('\u2019', '\'');  // Right single quote → apostrophe
        title = title.Replace('\u2018', '\'');  // Left single quote → apostrophe

        // Convert double underscore to colon (e.g., Topic__Subtitle → Topic: Subtitle)
        title = title.Replace("__", ": ");

        // Convert lonely _s_ or _s at end to possessive apostrophe (e.g., Valley_s_Wealth → Valley's Wealth)
        title = PossessiveRegex().Replace(title, "'s");

        title = title.Replace('_', ' ');

        // Handle PascalCase: Insert space before uppercase letters that follow lowercase letters
        // But preserve sequences like "2D", "3D", "4K" (digit followed by uppercase)
        title = PascalRegex().Replace(title, " ");

        // Collapse multiple spaces into single space
        title = SpaceRegex().Replace(title, " ");

        return title.Trim();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private DateTime GetPublishedDate(string filePath)
    {
        if (TryGetPublishedDateFromFile(filePath, out var date))
        {
            _logger.LogInformation("Using file metadata for published date: {Date}", date);

            return date.Value;
        }

        _logger.LogDebug("No metadata date found for {FilePath}, using current time", filePath);

        return DateTime.UtcNow;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("(?<![A-Z0-9-])(?=[A-Z])")]
    private static partial Regex PascalRegex();

    [GeneratedRegex(@"_s(?=_|$)")]
    private static partial Regex PossessiveRegex();
}
