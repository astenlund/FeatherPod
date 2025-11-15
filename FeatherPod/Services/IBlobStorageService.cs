namespace FeatherPod.Services;

public interface IBlobStorageService
{
    Task InitializeAsync();

    // Feed operations
    Task<string?> LoadFeedsConfigAsync();
    Task SaveFeedsConfigAsync(string feedsJson);

    // Audio file operations (feed-aware)
    Task UploadAudioAsync(string feedId, string fileName, string filePath);
    Task<Stream> DownloadAudioAsync(string feedId, string fileName);
    Task<bool> AudioExistsAsync(string feedId, string fileName);
    Task DeleteAudioAsync(string feedId, string fileName);
    Task<List<string>> ListAudioFilesAsync(string feedId);
    Task<long> GetAudioFileSizeAsync(string feedId, string fileName);
    Task<string> DownloadAudioToTempAsync(string feedId, string fileName);

    // Icon operations
    Task UploadIconAsync(string feedId, string filePath);
    Task<bool> IconExistsAsync(string feedId);
    Task<Stream> DownloadIconAsync(string feedId);

    // Episode metadata operations (feed-aware)
    Task SaveEpisodeMetadataAsync(string feedId, string metadataJson);
    Task<string?> LoadEpisodeMetadataAsync(string feedId);

    // Feed management operations
    Task RenameFeedAsync(string oldFeedId, string newFeedId);
    Task DeleteFeedAsync(string feedId);
}
