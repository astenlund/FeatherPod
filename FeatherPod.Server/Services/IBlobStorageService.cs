namespace FeatherPod.Server.Services;

public interface IBlobStorageService
{
    Task InitializeAsync();

    // Feed operations
    Task<string?> LoadFeedsConfigAsync();
    Task SaveFeedsConfigAsync(string feedsJson);

    // User operations
    Task<string?> LoadUsersConfigAsync();
    Task SaveUsersConfigAsync(string usersJson);

    // Audio file operations (feed-aware)
    Task UploadAudioAsync(string feedId, string fileName, string filePath);
    Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath, CancellationToken cancellationToken = default);
    Task<Stream> DownloadAudioAsync(string feedId, string fileName);
    Task<bool> AudioExistsAsync(string feedId, string fileName);
    Task DeleteAudioAsync(string feedId, string fileName);
    Task<List<string>> ListAudioFilesAsync(string feedId);
    Task<long> GetAudioFileSizeAsync(string feedId, string fileName);
    Task<string> DownloadAudioToTempAsync(string feedId, string fileName);
    Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length);

    // Icon operations
    Task UploadIconAsync(string feedId, string filePath);
    Task<string?> GetIconETagAsync(string feedId);
    Task<Stream> DownloadIconAsync(string feedId);
    Task DeleteIconAsync(string feedId);

    // Episode metadata operations (feed-aware)
    Task SaveEpisodeMetadataAsync(string feedId, string metadataJson);
    Task<string?> LoadEpisodeMetadataAsync(string feedId);

    // Pending blob operations
    Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName);
    Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName);
    Task<bool> PendingBlobExistsAsync(string feedId, string jobId, string fileName);
    Task DeletePendingJobBlobsAsync(string feedId, string jobId);

    /// <summary>
    /// Read-only SAS URL for a published episode audio blob (the source of a transcript retry's batch or URL-probe path).
    /// </summary>
    Task<string> GenerateAudioSasUrlAsync(string feedId, string fileName);

    /// <summary>
    /// Stage a transcription run's transcript under its job's pending blobs, scoped to the run's attempt. Only the job's join of that
    /// attempt moves it to the episode's transcript location, so a run that is no longer current never touches a published transcript.
    /// </summary>
    Task UploadPendingTranscriptAsync(string feedId, string jobId, int attempt, string vttContent);

    /// <summary>
    /// Read a transcript staged by <see cref="UploadPendingTranscriptAsync"/> for <paramref name="attempt"/>, or null when none exists.
    /// </summary>
    Task<string?> DownloadPendingTranscriptAsync(string feedId, string jobId, int attempt);

    // Push subscription operations (feed-aware)
    Task<string?> LoadPushSubscriptionsAsync(string feedId);
    Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson);

    // Transcript operations (feed-aware)
    Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent);
    Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId);
    Task DeleteTranscriptAsync(string feedId, string episodeId);

    // Feed management operations
    Task RenameFeedAsync(string oldFeedId, string newFeedId);
    Task DeleteFeedAsync(string feedId);
}
