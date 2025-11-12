using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Models;
using Microsoft.Extensions.Configuration;

namespace FeatherPod.Services;

public class EpisodeService : IDisposable
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<EpisodeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Episode> _episodes = new();

    public EpisodeService(IBlobStorageService blobStorage, IConfiguration configuration, ILogger<EpisodeService> logger)
    {
        _blobStorage = blobStorage;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await LoadEpisodesAsync();
        await SyncWithBlobStorageAsync();
    }

    public async Task<List<Episode>> GetAllEpisodesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _episodes.OrderByDescending(e => e.PublishedDate).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode?> GetEpisodeByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            return _episodes.FirstOrDefault(e => e.Id == id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Episode> AddEpisodeAsync(string filePath, string? title = null, string? description = null, DateTime? publishedDate = null, bool? useMetadataForPublishedDate = null)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Audio file not found", filePath);

        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Length;
        var id = Episode.GenerateId(fileName, fileSize);

        await _lock.WaitAsync();
        try
        {
            // Check if episode already exists
            var existing = _episodes.FirstOrDefault(e => e.Id == id);
            if (existing != null)
            {
                _logger.LogInformation("Episode with ID {Id} already exists, skipping", id);
                return existing;
            }

            var duration = await GetAudioDurationAsync(filePath);

            // Determine published date based on configuration and parameters
            // Priority order:
            // 1. Explicit publishedDate parameter (highest priority)
            // 2. useMetadataForPublishedDate parameter (per-request override)
            // 3. UseFileMetadataForPublishDate config setting
            // 4. Current datetime (default fallback)
            DateTime finalPublishedDate;
            if (publishedDate.HasValue)
            {
                // Explicit date provided, use it
                finalPublishedDate = publishedDate.Value;
                _logger.LogDebug("Using explicitly provided published date for {File}: {Date}", fileName, finalPublishedDate);
            }
            else if (useMetadataForPublishedDate.HasValue && useMetadataForPublishedDate.Value)
            {
                // Per-request override to use file metadata
                finalPublishedDate = GetPublishedDate(filePath);
                _logger.LogDebug("Using file metadata (per-request) for published date for {File}: {Date}", fileName, finalPublishedDate);
            }
            else
            {
                var podcastConfig = _configuration.GetSection("Podcast").Get<PodcastConfig>();
                if (podcastConfig?.UseFileMetadataForPublishDate == true)
                {
                    // Use file metadata based on global config
                    finalPublishedDate = GetPublishedDate(filePath);
                    _logger.LogDebug("Using file metadata (config) for published date for {File}: {Date}", fileName, finalPublishedDate);
                }
                else
                {
                    // Default: use current datetime
                    finalPublishedDate = DateTime.UtcNow;
                    _logger.LogDebug("Using current datetime for published date for {File}: {Date}", fileName, finalPublishedDate);
                }
            }

            var episode = new Episode
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(title) ? ParseTitleFromFilename(fileName) : title,
                Description = description ?? string.Empty,
                FileName = fileName,
                FileSize = fileSize,
                Duration = duration,
                PublishedDate = finalPublishedDate
            };

            // Upload file to blob storage
            await _blobStorage.UploadAudioAsync(fileName, filePath);

            _episodes.Add(episode);
            await SaveEpisodesAsync();

            _logger.LogInformation("Added episode: {Title} ({Id})", episode.Title, episode.Id);
            return episode;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteEpisodeAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var episode = _episodes.FirstOrDefault(e => e.Id == id);
            if (episode == null)
                return false;

            // Delete audio file from blob storage
            await _blobStorage.DeleteAudioAsync(episode.FileName);

            _episodes.Remove(episode);
            await SaveEpisodesAsync();

            _logger.LogInformation("Deleted episode: {Title} ({Id})", episode.Title, episode.Id);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SyncWithBlobStorageAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var audioFiles = await _blobStorage.ListAudioFilesAsync();
            var changesMade = false;

            // Remove episodes whose files no longer exist in blob storage
            var toRemove = _episodes
                .Where(e => !audioFiles.Contains(e.FileName))
                .ToList();

            foreach (var episode in toRemove)
            {
                _episodes.Remove(episode);
                _logger.LogInformation("Removed episode {Title} - file no longer exists in blob storage", episode.Title);
                changesMade = true;
            }

            // Add new files found in blob storage
            var existingFileNames = _episodes.Select(e => e.FileName).ToHashSet();
            var newFiles = audioFiles
                .Where(f => IsAudioFile(f) && !existingFileNames.Contains(f))
                .ToList();

            foreach (var fileName in newFiles)
            {
                try
                {
                    // Download to temp file for metadata extraction
                    var tempPath = await _blobStorage.DownloadAudioToTempAsync(fileName);

                    try
                    {
                        var fileSize = await _blobStorage.GetAudioFileSizeAsync(fileName);
                        var id = Episode.GenerateId(fileName, fileSize);

                        // Check if episode with this ID already exists (shouldn't happen, but safety check)
                        if (_episodes.Any(e => e.Id == id))
                        {
                            _logger.LogDebug("Episode with ID {Id} already exists, skipping {FileName}", id, fileName);
                            continue;
                        }

                        var duration = await GetAudioDurationAsync(tempPath);
                        var publishedDate = GetPublishedDate(tempPath);

                        var episode = new Episode
                        {
                            Id = id,
                            Title = ParseTitleFromFilename(fileName),
                            Description = string.Empty,
                            FileName = fileName,
                            FileSize = fileSize,
                            Duration = duration,
                            PublishedDate = publishedDate
                        };

                        _episodes.Add(episode);
                        _logger.LogInformation("Auto-imported episode from blob storage: {Title} ({Id})", episode.Title, episode.Id);
                        changesMade = true;
                    }
                    finally
                    {
                        // Clean up temp file
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error auto-importing file {File} from blob storage", fileName);
                }
            }

            if (changesMade)
            {
                await SaveEpisodesAsync();
            }

            _logger.LogInformation("Sync complete: {Count} episodes active", _episodes.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadEpisodesAsync()
    {
        try
        {
            var json = await _blobStorage.LoadMetadataAsync();
            if (json == null)
            {
                _episodes = new List<Episode>();
                _logger.LogInformation("No metadata found in blob storage, starting fresh");
                return;
            }

            _episodes = JsonSerializer.Deserialize<List<Episode>>(json) ?? new List<Episode>();
            _logger.LogInformation("Loaded {Count} episodes from metadata", _episodes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading episodes metadata, starting fresh");
            _episodes = new List<Episode>();
        }
    }

    private async Task SaveEpisodesAsync()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(_episodes, options);
        await _blobStorage.SaveMetadataAsync(json);
    }

    private Task<TimeSpan> GetAudioDurationAsync(string filePath)
    {
        return Task.Run(() =>
        {
            try
            {
                using var file = TagLib.File.Create(filePath);
                return file.Properties.Duration;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read audio duration from {File}, using default", filePath);
                return TimeSpan.Zero;
            }
        });
    }

    private DateTime GetPublishedDate(string filePath)
    {
        // Priority order:
        // 1. TagLib DateTagged from audio metadata (user-editable, all formats)
        // 2. MP4 container creation time (mvhd box) - for M4A/MP4 files
        // 3. File modified date
        // 4. File created date
        // 5. Current time

        // Try to get media created date from tag (highest priority)
        try
        {
            using var file = TagLib.File.Create(filePath);

            if (file.Tag.DateTagged.HasValue && file.Tag.DateTagged.Value > DateTime.MinValue)
            {
                _logger.LogDebug("Using DateTagged for {File}: {Date}", filePath, file.Tag.DateTagged.Value);
                return file.Tag.DateTagged.Value.ToUniversalTime();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read metadata date from {File}", filePath);
        }

        // For M4A/MP4 files, try to get creation time from container metadata
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is ".m4a" or ".mp4")
        {
            var mp4CreationTime = Mp4Parser.GetCreationTime(filePath);
            if (mp4CreationTime.HasValue && mp4CreationTime.Value > DateTime.MinValue)
            {
                _logger.LogDebug("Using MP4 container creation time for {File}: {Date}", filePath, mp4CreationTime.Value);
                return mp4CreationTime.Value;
            }
        }

        try
        {
            var fileInfo = new FileInfo(filePath);

            // Try file modified date
            if (fileInfo.LastWriteTimeUtc > DateTime.MinValue)
            {
                _logger.LogDebug("Using LastWriteTime for {File}: {Date}", filePath, fileInfo.LastWriteTimeUtc);
                return fileInfo.LastWriteTimeUtc;
            }

            // Try file created date
            if (fileInfo.CreationTimeUtc > DateTime.MinValue)
            {
                _logger.LogDebug("Using CreationTime for {File}: {Date}", filePath, fileInfo.CreationTimeUtc);
                return fileInfo.CreationTimeUtc;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read file dates from {File}", filePath);
        }

        // Fallback to current time
        _logger.LogDebug("Using current time for {File}", filePath);
        return DateTime.UtcNow;
    }

    private static bool IsAudioFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".mp3" or ".m4a" or ".wav" or ".ogg" or ".flac" or ".aac";
    }

    internal static string ParseTitleFromFilename(string filename)
    {
        // Get filename without extension
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);

        // Replace underscores with spaces
        var withSpaces = nameWithoutExtension.Replace('_', ' ');

        // Insert spaces before uppercase letters (PascalCase handling)
        // This regex inserts a space before any uppercase letter that follows a lowercase letter or digit
        var withPascalCaseSpaces = Regex.Replace(withSpaces, @"(?<=[a-z0-9])(?=[A-Z])", " ");

        // Clean up multiple spaces
        var cleaned = Regex.Replace(withPascalCaseSpaces, @"\s+", " ").Trim();

        return cleaned;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
