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
    Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath);
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
    Task DeletePendingJobBlobsAsync(string feedId, string jobId);

    // Push subscription operations (feed-aware)
    Task<string?> LoadPushSubscriptionsAsync(string feedId);
    Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson);

    // Feed management operations
    Task RenameFeedAsync(string oldFeedId, string newFeedId);
    Task DeleteFeedAsync(string feedId);
}
