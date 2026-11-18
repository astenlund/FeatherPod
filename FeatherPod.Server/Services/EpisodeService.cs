using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

public sealed partial class EpisodeService : IDisposable
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<EpisodeService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Feed ID → List of Episodes
    private readonly Dictionary<string, List<Episode>> _episodesByFeed = new();
    private readonly Dictionary<string, FeedVersionState> _feedVersionByFeed = new();
    private static readonly DateTime ServerStartTime = DateTime.UtcNow;

    private record FeedVersionState(DateTime ModifiedAt, string? LastSyncJson);
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
            BumpFeedModifiedAt(feedConfig.Id);

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
            BumpFeedModifiedAt(feedId);

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
        // Validate under the lock, then rename blobs outside it before committing the in-memory change.
        await _lock.WaitAsync();
        try
        {
            if (_feedsMetadata.Feeds.All(f => f.Id != oldFeedId))
            {
                throw new InvalidOperationException($"Feed '{oldFeedId}' not found");
            }

            if (_feedsMetadata.Feeds.Any(f => f.Id == newFeedId))
            {
                throw new InvalidOperationException($"Feed with ID '{newFeedId}' already exists");
            }
        }
        finally
        {
            _lock.Release();
        }

        // Rename in blob storage first so a failure aborts before any in-memory change. Moved audio blobs are new blob versions.
        var renamedAudioETags = await _blobStorage.RenameFeedAsync(oldFeedId, newFeedId);

        string? commitConflict = null;

        await _lock.WaitAsync();
        try
        {
            // Re-validate at commit: a concurrent rename or create can have claimed either
            // id while the lock was released for the blob rename above.
            var feed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == oldFeedId);
            if (feed == null)
            {
                commitConflict = $"Feed '{oldFeedId}' not found";
            }
            else if (_feedsMetadata.Feeds.Any(f => f.Id == newFeedId))
            {
                commitConflict = $"Feed with ID '{newFeedId}' already exists";
            }
            else
            {
                // Update feed config
                var updatedFeed = feed with { Id = newFeedId };
                var feeds = _feedsMetadata.Feeds.Where(f => f.Id != oldFeedId).Append(updatedFeed).ToList();
                _feedsMetadata = new() { Feeds = feeds };

                // Update episodes in memory
                if (_episodesByFeed.TryGetValue(oldFeedId, out var episodes))
                {
                    // Update each episode's FeedId, and its audio ownership to the moved blob
                    var updatedEpisodes = episodes.Select(e => WithRenamedAudioETag(e, renamedAudioETags) with { FeedId = newFeedId }).ToList();
                    _episodesByFeed.Remove(oldFeedId);
                    _episodesByFeed[newFeedId] = updatedEpisodes;
                }

                RemoveFeedVersionTracking(oldFeedId);
                BumpFeedModifiedAt(newFeedId);

                await SaveFeedsAsync();

                // The blob rename copied the old episodes.json byte for byte; persist the new feed id and audio ETags over it, or the
                // next sync or restart reloads the stale copy
                await SaveEpisodesAsync(newFeedId);

                _logger.LogInformation("Renamed feed: {OldId} -> {NewId}", oldFeedId, newFeedId);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (commitConflict != null)
        {
            // The blobs were already renamed; rename them back so storage matches the
            // unchanged metadata before surfacing the conflict.
            var restoredAudioETags = await _blobStorage.RenameFeedAsync(newFeedId, oldFeedId);
            await RecordRestoredAudioETagsAsync(oldFeedId, restoredAudioETags);

            throw new InvalidOperationException(commitConflict);
        }
    }

    /// <summary>
    /// The episode with its audio ownership moved to the ETag its audio blob got in a feed rename. An episode whose blob the rename
    /// did not move keeps its ETag, which then matches no blob, so a later delete leaves whatever is at the path alone.
    /// </summary>
    private static Episode WithRenamedAudioETag(Episode episode, IReadOnlyDictionary<string, string> renamedAudioETags) =>
        renamedAudioETags.TryGetValue(episode.FileName, out var eTag) ? episode with { AudioETag = eTag } : episode;

    /// <summary>
    /// After a conflicting feed rename was rolled back, record the ETags the rolled-back audio blobs got on the feed's episodes.
    /// </summary>
    private async Task RecordRestoredAudioETagsAsync(string feedId, IReadOnlyDictionary<string, string> restoredAudioETags)
    {
        await _lock.WaitAsync();
        try
        {
            if (_episodesByFeed.TryGetValue(feedId, out var episodes) && episodes.Count > 0)
            {
                _episodesByFeed[feedId] = episodes.Select(e => WithRenamedAudioETag(e, restoredAudioETags)).ToList();
                await SaveEpisodesAsync(feedId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Record that the audio blob at <paramref name="fileName"/> now has <paramref name="eTag"/> on every episode of the feed that
    /// serves it (episodes sharing a file name share its blob). Caller holds the lock. A null ETag (a writer that did not report
    /// one) records nothing.
    /// </summary>
    private static void RecordAudioWrite(List<Episode> episodes, string fileName, string? eTag)
    {
        if (eTag == null)
        {
            return;
        }

        for (var i = 0; i < episodes.Count; i++)
        {
            if (episodes[i].FileName == fileName && episodes[i].AudioETag != eTag)
            {
                episodes[i] = episodes[i] with { AudioETag = eTag };
            }
        }
    }

    public async Task DeleteFeedAsync(string feedId)
    {
        // Validate under the lock, delete blobs outside it, then commit the in-memory removal.
        await _lock.WaitAsync();
        try
        {
            if (_feedsMetadata.Feeds.All(f => f.Id != feedId))
            {
                throw new InvalidOperationException($"Feed '{feedId}' not found");
            }
        }
        finally
        {
            _lock.Release();
        }

        // Delete from blob storage first so a failure aborts before any in-memory change.
        await _blobStorage.DeleteFeedAsync(feedId);

        await _lock.WaitAsync();
        try
        {
            // Remove from feeds list
            _feedsMetadata = _feedsMetadata with
            {
                Feeds = _feedsMetadata.Feeds.Where(f => f.Id != feedId).ToList()
            };

            // Remove episodes from memory
            _episodesByFeed.Remove(feedId);
            RemoveFeedVersionTracking(feedId);

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

    public async Task<(FeedConfig Feed, List<Episode> Episodes, DateTime LastModified)?> GetFeedSnapshotAsync(string feedId)
    {
        await _lock.WaitAsync();

        try
        {
            var feed = _feedsMetadata.Feeds.FirstOrDefault(f => f.Id == feedId);
            if (feed == null || !_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                return null;
            }

            var lastModified = _feedVersionByFeed.GetValueOrDefault(feedId)?.ModifiedAt ?? ServerStartTime;

            return (feed, episodes.OrderByDescending(e => e.PublishedDate).ToList(), lastModified);
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
        MediaSource? mediaSource = null,
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

        // Do all I/O outside the lock to avoid blocking other operations
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

        var audioETag = await _blobStorage.UploadAudioAsync(feedId, fileName, filePath);

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
            MediaSource = mediaSource,
            UploadedAt = DateTime.UtcNow,
            AudioETag = audioETag
        };

        // Acquire lock only for in-memory state mutation
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

            RecordAudioWrite(episodes, fileName, audioETag);
            episodes.Add(episode);
            BumpFeedModifiedAt(feedId);
            await SaveEpisodesAsync(feedId);

            _logger.LogInformation("Added episode to feed {FeedId}: {Title} ({FileName})", feedId, episode.Title, fileName);

            return episode;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Add a pre-built episode to the feed cache and save to blob storage.
    /// Used by the join logic when both normalization and transcription tracks are complete.
    /// The audio blob is already uploaded by the Function; this updates episodes.json and, when <paramref name="transcriptVtt"/> is
    /// given, first writes the episode's transcript. The transcript write happens inside the lock, against the usual rule of I/O outside
    /// it, because every write of an episode's transcript location by a join (here and <see cref="PublishTranscriptIfAsync"/>) must be
    /// ordered with the others and with the episode's own changes, or one join could replace a transcript another just published.
    /// A failed transcript write degrades only the transcript: the episode is still added, with a failed transcript the user can
    /// retry, rather than lost. Returns the episode as stored.
    /// </summary>
    public async Task<Episode> AddEpisodeFromEntityAsync(Episode episode, CancellationToken cancellationToken = default, string? transcriptVtt = null)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (transcriptVtt != null)
            {
                try
                {
                    await _blobStorage.UploadTranscriptAsync(episode.FeedId, episode.Id, transcriptVtt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to write the transcript of episode {Id} in feed {FeedId}; adding it with a failed transcript", episode.Id, episode.FeedId);
                    episode = episode with { TranscriptStatus = TranscriptStatus.Failed };
                }
            }

            if (!_episodesByFeed.TryGetValue(episode.FeedId, out var episodes))
            {
                episodes = [];
                _episodesByFeed[episode.FeedId] = episodes;
            }

            var existing = episodes.FirstOrDefault(e => e.Id == episode.Id);
            if (existing != null)
            {
                _logger.LogInformation("Episode {Id} already exists in feed {FeedId}, replacing", episode.Id, episode.FeedId);
                episodes.Remove(existing);
            }

            RecordAudioWrite(episodes, episode.FileName, episode.AudioETag);
            episodes.Add(episode);
            BumpFeedModifiedAt(episode.FeedId);
            await SaveEpisodesAsync(episode.FeedId);

            _logger.LogInformation("Added episode from entity to feed {FeedId}: {Title} ({FileName})", episode.FeedId, episode.Title, episode.FileName);

            return episode;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteEpisodeAsync(string feedId, string id)
    {
        Episode episode;
        bool deleteAudioBlob;

        // Remove from metadata and decide blob deletion in the SAME critical section:
        // the shared-file check is only correct while no concurrent mutation can slip
        // between the decision and the removal (concurrent deletes of two episodes
        // sharing a FileName would otherwise both see the other and both skip the blob).
        await _lock.WaitAsync();
        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var value))
            {
                return false;
            }

            var found = value.FirstOrDefault(e => e.Id == id);
            if (found == null)
            {
                return false;
            }

            episode = found;
            value.Remove(episode);

            // Only delete the blob if no remaining episode references the same file
            deleteAudioBlob = !value.Any(e => e.FileName == episode.FileName);

            BumpFeedModifiedAt(feedId);
            await SaveEpisodesAsync(feedId);

            // The transcript is deleted inside the commit, like every other write of an episode's transcript location: outside the
            // lock, a re-upload re-creating this id could publish its own transcript before the delete lands
            if (episode.TranscriptStatus == TranscriptStatus.Available)
            {
                try
                {
                    await _blobStorage.DeleteTranscriptAsync(feedId, episode.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Episode {Id} removed but its transcript delete failed; it remains as an orphan", episode.Id);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        // The audio delete runs after the commit so readers never observe an episode whose enclosure points at an already-deleted
        // blob, and only while the blob is still the one this episode owns: a re-upload's normalization writes the same path outside
        // this lock at any time, before its join publishes it. A failure or a replaced blob therefore leaves at most an orphan.
        if (deleteAudioBlob)
        {
            await DeleteOwnedAudioAsync(feedId, episode.FileName, episode.AudioETag);
        }
        else
        {
            _logger.LogInformation("Skipping blob deletion - another episode references {FileName}", episode.FileName);
        }

        _logger.LogInformation("Deleted episode from feed {FeedId}: {Title}", feedId, episode.Title);

        return true;
    }

    public Task<Episode?> UpdateEpisodeTitleAsync(string feedId, string id, string newTitle)
    {
        return UpdateEpisodeMetadataAsync(feedId, id, title: newTitle);
    }

    /// <summary>
    /// Renames an episode only while its title is still <paramref name="expectedTitle"/>, comparing inside the lock so a
    /// rename made in the meantime is never overwritten. Returns the renamed episode, or null when the episode does not
    /// exist or its title has changed.
    /// </summary>
    public async Task<Episode?> RenameEpisodeIfTitleIsAsync(string feedId, string id, string expectedTitle, string newTitle)
    {
        await _lock.WaitAsync();

        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                return null;
            }

            var index = episodes.FindIndex(e => e.Id == id);
            if (index < 0 || !string.Equals(episodes[index].Title, expectedTitle, StringComparison.Ordinal))
            {
                return null;
            }

            var updated = episodes[index] with { Title = newTitle };
            episodes[index] = updated;
            BumpFeedModifiedAt(feedId);
            await SaveEpisodesAsync(feedId);
            _logger.LogInformation("Updated episode title in feed {FeedId}: {OldTitle} -> {NewTitle}", feedId, expectedTitle, newTitle);

            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Publish a retried transcript: inside the lock, only while the episode exists, is still the incarnation uploaded at
    /// <paramref name="expectedUploadedAt"/> (a delete and re-upload creates a new one under the same id) and still has a failed
    /// transcript, run <paramref name="writeTranscript"/> to write its transcript location, then mark it Available and bump the feed
    /// version so conditional fetches see the change. The write runs inside the lock for the reason given on
    /// <see cref="AddEpisodeFromEntityAsync"/>. Returns false, writing nothing, when any condition fails.
    /// </summary>
    public async Task<bool> PublishTranscriptIfAsync(string feedId, string id, DateTime expectedUploadedAt, Func<Task> writeTranscript)
    {
        await _lock.WaitAsync();

        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                return false;
            }

            var index = episodes.FindIndex(e => e.Id == id);
            if (index < 0 || episodes[index].TranscriptStatus != TranscriptStatus.Failed || episodes[index].UploadedAt.ToUniversalTime() != expectedUploadedAt.ToUniversalTime())
            {
                return false;
            }

            await writeTranscript();
            var previous = episodes[index];
            var previousVersion = _feedVersionByFeed.GetValueOrDefault(feedId);
            episodes[index] = previous with { TranscriptStatus = TranscriptStatus.Available };
            BumpFeedModifiedAt(feedId);
            try
            {
                await SaveEpisodesAsync(feedId);
            }
            catch
            {
                // Not published: the cache must not run ahead of the persisted metadata, which still says Failed, so the episode stays
                // retryable. The transcript blob just written is a leftover the next retry overwrites.
                episodes[index] = previous;
                if (previousVersion == null)
                {
                    _feedVersionByFeed.Remove(feedId);
                }
                else
                {
                    _feedVersionByFeed[feedId] = previousVersion;
                }

                throw;
            }

            _logger.LogInformation("Published retried transcript of episode {EpisodeId} in feed {FeedId}", id, feedId);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whether the episode exists as the incarnation uploaded at <paramref name="expectedUploadedAt"/> (the comparison
    /// <see cref="PublishTranscriptIfAsync"/> makes) with an available transcript.
    /// </summary>
    public async Task<bool> HasAvailableTranscriptAsync(string feedId, string id, DateTime expectedUploadedAt)
    {
        await _lock.WaitAsync();

        try
        {
            return _episodesByFeed.TryGetValue(feedId, out var episodes) &&
                episodes.Any(e => e.Id == id && e.TranscriptStatus == TranscriptStatus.Available && e.UploadedAt.ToUniversalTime() == expectedUploadedAt.ToUniversalTime());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode?> UpdateEpisodeMetadataAsync(string feedId, string id, string? title = null, string? note = null)
    {
        await _lock.WaitAsync();

        try
        {
            if (!_episodesByFeed.TryGetValue(feedId, out var episodes))
            {
                return null;
            }

            var index = episodes.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return null;
            }

            var existing = episodes[index];
            var updated = existing with
            {
                Title = title ?? existing.Title,
                Note = note ?? existing.Note,
            };
            episodes[index] = updated;
            BumpFeedModifiedAt(feedId);
            await SaveEpisodesAsync(feedId);

            if (title != null)
            {
                _logger.LogInformation("Updated episode title in feed {FeedId}: {OldTitle} -> {NewTitle}", feedId, existing.Title, title);
            }

            if (note != null)
            {
                _logger.LogInformation("Updated episode note in feed {FeedId}, episode {EpisodeId}", feedId, id);
            }

            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Move an episode to another feed. Refused with <see cref="EpisodeConflictException"/>, changing neither feed, when the target
    /// already has an episode or an audio blob with the file name. The commit moves only the incarnation the move started on (found
    /// by id and upload time), carrying its state as it is at the commit; when that incarnation was deleted or replaced the move
    /// publishes nothing. The source audio is deleted after the commit, only while it is still the version the episode owns (its
    /// <see cref="Episode.AudioETag"/>, or for an episode stored before ownership was recorded the version the move copied), so a
    /// blob a same-name upload wrote is left for that upload's join.
    /// </summary>
    public async Task<Episode> MoveEpisodeAsync(string episodeId, string sourceFeedId, string targetFeedId)
    {
        var episode = await SnapshotForTransferAsync(episodeId, sourceFeedId, targetFeedId);
        var (sourceETag, targetETag) = await CopyAudioToTargetAsync(episode, sourceFeedId, targetFeedId);
        var newId = Episode.GenerateId(targetFeedId, episode.FileName, episode.FileSize);

        Episode? movedEpisode = null;
        var deleteSourceAudio = false;
        string? ownedSourceETag = null;
        await _lock.WaitAsync();
        try
        {
            var current = FindIncarnation(sourceFeedId, episode);
            if (current != null)
            {
                var targetEpisodes = TargetEpisodesFor(targetFeedId, episode.FileName);

                // Copied inside the lock like the joins' transcript writes, from the state at commit: the transcript may have been
                // published during the blob I/O above
                await CopyTranscriptAsync(current, sourceFeedId, targetFeedId, newId);

                movedEpisode = current with
                {
                    Id = newId,
                    FeedId = targetFeedId,
                    AudioETag = targetETag
                };

                var sourceEpisodes = _episodesByFeed[sourceFeedId];
                sourceEpisodes.Remove(current);
                deleteSourceAudio = !sourceEpisodes.Any(e => e.FileName == episode.FileName);

                // The blob version the episode owns; one stored before ownership was recorded owns the version the move copied
                ownedSourceETag = current.AudioETag ?? sourceETag;
                targetEpisodes.Add(movedEpisode);
                BumpFeedModifiedAt(sourceFeedId);
                BumpFeedModifiedAt(targetFeedId);

                await SaveEpisodesAsync(sourceFeedId);
                await SaveEpisodesAsync(targetFeedId);

                // The source transcript is deleted inside the commit, like every other write of an episode's transcript location:
                // outside the lock, a re-upload re-creating the source id could publish its own transcript before this delete
                if (movedEpisode.TranscriptStatus == TranscriptStatus.Available)
                {
                    try
                    {
                        await _blobStorage.DeleteTranscriptAsync(sourceFeedId, episode.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to delete the source transcript of moved episode {Id}; it remains as an orphan", episode.Id);
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Refused at commit (a target conflict, or the target feed renamed or deleted meanwhile): nothing published
            LogTargetCopyOrphaned(episodeId, targetFeedId, episode.FileName);

            throw;
        }
        finally
        {
            _lock.Release();
        }

        if (movedEpisode == null)
        {
            // Deleted or replaced during the move: nothing is published to the target, and a re-upload at the source is left alone
            LogTargetCopyOrphaned(episodeId, targetFeedId, episode.FileName);

            throw new InvalidOperationException($"Episode '{episodeId}' was deleted or replaced while it was being moved");
        }

        if (deleteSourceAudio)
        {
            await DeleteOwnedAudioAsync(sourceFeedId, episode.FileName, ownedSourceETag);
        }

        _logger.LogInformation("Moved episode {Id} from feed {Source} to {Target}", episodeId, sourceFeedId, targetFeedId);

        return movedEpisode;
    }

    /// <summary>
    /// Copy an episode to another feed, with the refusals of <see cref="MoveEpisodeAsync"/>. The commit copies the incarnation the
    /// copy started on, carrying its state as it is at the commit; when that incarnation was deleted or replaced nothing is published.
    /// </summary>
    public async Task<Episode> CopyEpisodeAsync(string episodeId, string sourceFeedId, string targetFeedId)
    {
        var episode = await SnapshotForTransferAsync(episodeId, sourceFeedId, targetFeedId);
        var (_, targetETag) = await CopyAudioToTargetAsync(episode, sourceFeedId, targetFeedId);
        var newId = Episode.GenerateId(targetFeedId, episode.FileName, episode.FileSize);

        Episode? copiedEpisode = null;
        await _lock.WaitAsync();
        try
        {
            var current = FindIncarnation(sourceFeedId, episode);
            if (current != null)
            {
                var targetEpisodes = TargetEpisodesFor(targetFeedId, episode.FileName);
                await CopyTranscriptAsync(current, sourceFeedId, targetFeedId, newId);

                copiedEpisode = current with
                {
                    Id = newId,
                    FeedId = targetFeedId,
                    AudioETag = targetETag
                };

                targetEpisodes.Add(copiedEpisode);
                BumpFeedModifiedAt(targetFeedId);

                await SaveEpisodesAsync(targetFeedId);
            }
        }
        catch (InvalidOperationException)
        {
            // Refused at commit (a target conflict, or the target feed renamed or deleted meanwhile): nothing published
            LogTargetCopyOrphaned(episodeId, targetFeedId, episode.FileName);

            throw;
        }
        finally
        {
            _lock.Release();
        }

        if (copiedEpisode == null)
        {
            LogTargetCopyOrphaned(episodeId, targetFeedId, episode.FileName);

            throw new InvalidOperationException($"Episode '{episodeId}' was deleted or replaced while it was being copied");
        }

        _logger.LogInformation("Copied episode {Id} from feed {Source} to {Target}", episodeId, sourceFeedId, targetFeedId);

        return copiedEpisode;
    }

    /// <summary>
    /// The episode a move or copy starts from, after checking both feeds exist and the target has no episode with its file name.
    /// </summary>
    private async Task<Episode> SnapshotForTransferAsync(string episodeId, string sourceFeedId, string targetFeedId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_feedsMetadata.Feeds.All(f => f.Id != sourceFeedId))
            {
                throw new InvalidOperationException($"Source feed '{sourceFeedId}' not found");
            }

            var episode = _episodesByFeed[sourceFeedId].FirstOrDefault(e => e.Id == episodeId)
                ?? throw new InvalidOperationException($"Episode '{episodeId}' not found in feed '{sourceFeedId}'");
            TargetEpisodesFor(targetFeedId, episode.FileName);

            return episode;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Copy the episode's audio, the bytes now at its path, to the target feed, create-only, so an existing blob there (an orphan, or
    /// an upload's normalization that has not been joined yet) is never overwritten. Refused with
    /// <see cref="EpisodeConflictException"/> when the target path is taken. Returns the ETags of the source version copied and of
    /// the new target blob.
    /// </summary>
    private async Task<(string SourceETag, string TargetETag)> CopyAudioToTargetAsync(Episode episode, string sourceFeedId, string targetFeedId)
    {
        var download = await _blobStorage.DownloadAudioToTempAsync(sourceFeedId, episode.FileName);
        try
        {
            var targetETag = await _blobStorage.CreateAudioAsync(targetFeedId, episode.FileName, download.Path)
                ?? throw new EpisodeConflictException(EpisodeConflictReason.TargetPathTaken, $"Feed '{targetFeedId}' already has a file named '{episode.FileName}'");

            return (download.ETag, targetETag);
        }
        finally
        {
            File.Delete(download.Path);
        }
    }

    /// <summary>
    /// The source episode as it is now, when it is still the incarnation <paramref name="snapshot"/> was taken from (same id and
    /// upload time; a delete and re-upload creates a new one under the same id), or null. Caller holds the lock.
    /// </summary>
    private Episode? FindIncarnation(string feedId, Episode snapshot) =>
        _episodesByFeed.TryGetValue(feedId, out var episodes)
            ? episodes.FirstOrDefault(e => e.Id == snapshot.Id && e.UploadedAt.ToUniversalTime() == snapshot.UploadedAt.ToUniversalTime())
            : null;

    /// <summary>
    /// The target feed's episode list for a move or copy of <paramref name="fileName"/>, checked before the copy and again at commit:
    /// refused when the feed no longer exists (renamed or deleted meanwhile) and, with <see cref="EpisodeConflictException"/>, when it
    /// already has an episode with the file name. Caller holds the lock.
    /// </summary>
    private List<Episode> TargetEpisodesFor(string targetFeedId, string fileName)
    {
        if (_feedsMetadata.Feeds.All(f => f.Id != targetFeedId))
        {
            throw new InvalidOperationException($"Target feed '{targetFeedId}' not found");
        }

        if (!_episodesByFeed.TryGetValue(targetFeedId, out var episodes))
        {
            episodes = [];
            _episodesByFeed[targetFeedId] = episodes;
        }

        if (episodes.Any(e => e.FileName == fileName))
        {
            throw new EpisodeConflictException(EpisodeConflictReason.TargetHasEpisode, $"Feed '{targetFeedId}' already has an episode with the file '{fileName}'");
        }

        return episodes;
    }

    /// <summary>
    /// A move or copy that published nothing after copying its audio leaves the copy, and any transcript, as orphans rather than
    /// deleting them: an upload of the same file can write and publish its own audio or transcript at those paths at any point (an
    /// orphan is the safe failure, a dangling reference is not).
    /// </summary>
    private void LogTargetCopyOrphaned(string episodeId, string targetFeedId, string fileName) =>
        _logger.LogWarning("Episode {Id} was not published to feed {Target}; its copied audio {File} and any transcript remain there as orphans", episodeId, targetFeedId, fileName);

    /// <summary>
    /// Delete an episode's audio after its commit, only while the blob still has <paramref name="ownedETag"/> (unconditionally when
    /// the episode predates ownership tracking). A replaced blob or a failure leaves an orphan, never a dangling reference.
    /// </summary>
    private async Task DeleteOwnedAudioAsync(string feedId, string fileName, string? ownedETag)
    {
        try
        {
            if (!await _blobStorage.DeleteAudioAsync(feedId, fileName, ownedETag))
            {
                _logger.LogInformation("Kept audio {FeedId}/{FileName}: another writer replaced it after it was released", feedId, fileName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Episode removed but blob cleanup failed for {FeedId}/{FileName}; blob may be orphaned", feedId, fileName);
        }
    }

    private async Task CopyTranscriptAsync(Episode episode, string sourceFeedId, string targetFeedId, string targetEpisodeId)
    {
        if (episode.TranscriptStatus != TranscriptStatus.Available)
        {
            return;
        }

        var transcriptStream = await _blobStorage.DownloadTranscriptAsync(sourceFeedId, episode.Id);
        if (transcriptStream != null)
        {
            await using (transcriptStream)
            {
                using var reader = new StreamReader(transcriptStream);
                var vttContent = await reader.ReadToEndAsync();
                await _blobStorage.UploadTranscriptAsync(targetFeedId, targetEpisodeId, vttContent);
            }
        }
    }

    public async Task SyncWithBlobStorageAsync(string feedId)
    {
        // Fetch data outside the lock to avoid blocking reads during blob I/O
        var metadataJson = await _blobStorage.LoadEpisodeMetadataAsync(feedId);
        var episodes = metadataJson != null
            ? JsonSerializer.Deserialize<List<Episode>>(metadataJson) ?? []
            : [];
        var blobFiles = await _blobStorage.ListAudioFilesAsync(feedId);

        // Acquire lock only for in-memory state update
        await _lock.WaitAsync();
        try
        {
            _episodesByFeed[feedId] = episodes;

            var currentState = _feedVersionByFeed.GetValueOrDefault(feedId);
            if (!string.Equals(metadataJson, currentState?.LastSyncJson, StringComparison.Ordinal))
            {
                BumpFeedModifiedAt(feedId);
            }

            // Update LastSyncJson to current blob content (preserve modifiedAt from BumpFeedModifiedAt if it ran)
            var updatedState = _feedVersionByFeed.GetValueOrDefault(feedId);
            if (updatedState != null)
            {
                _feedVersionByFeed[feedId] = updatedState with { LastSyncJson = metadataJson };
            }
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("Reloaded {Count} episodes for feed {FeedId} from blob storage", episodes.Count, feedId);

        // Warn about episodes whose blob files are missing (don't auto-delete to prevent silent data loss)
        var orphanedEpisodes = episodes
            .Where(e => !blobFiles.Contains(e.FileName))
            .ToList();

        foreach (var episode in orphanedEpisodes)
        {
            _logger.LogWarning("Episode has missing blob file in feed {FeedId}: {Title} ({FileName})", feedId, episode.Title, episode.FileName);
        }

        _logger.LogInformation("Sync complete for feed {FeedId}. Found {Count} episodes with missing files.", feedId, orphanedEpisodes.Count);
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

            _feedVersionByFeed[feed.Id] = new FeedVersionState(ServerStartTime, metadataJson);
        }
    }

    private void BumpFeedModifiedAt(string feedId)
    {
        var lastSyncJson = _feedVersionByFeed.GetValueOrDefault(feedId)?.LastSyncJson;

        _feedVersionByFeed[feedId] = new FeedVersionState(DateTime.UtcNow, lastSyncJson);
    }

    private void RemoveFeedVersionTracking(string feedId)
    {
        _feedVersionByFeed.Remove(feedId);
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
            if (file.Tag.DateTagged.HasValue)
            {
                date = file.Tag.DateTagged.Value.ToUniversalTime();

                return true;
            }
        }
        catch
        {
            // Unreadable date tags do not prevent independent container-date extraction.
        }

        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            date = Mp4Parser.GetCreationTime(filePath);

            return date.HasValue;
        }

        date = null;

        return false;
    }

    public static async Task<string> GenerateTitleAsync(string fileName, IAiService aiService, CancellationToken cancellationToken = default)
    {
        if (aiService.IsAvailable)
        {
            var aiTitle = await aiService.SuggestTitleAsync(fileName, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(aiTitle))
            {
                return aiTitle;
            }
        }

        return ParseTitleFromFilename(fileName);
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

        // Fix Apple brand names broken by PascalCase splitting
        title = title.Replace("i Pad", "iPad")
                     .Replace("i Phone", "iPhone")
                     .Replace("i OS", "iOS")
                     .Replace("Mac Book", "MacBook");

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
