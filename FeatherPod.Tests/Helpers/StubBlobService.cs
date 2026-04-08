using FeatherPod.Server.Services;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// No-op <see cref="IBlobStorageService"/> for unit tests that don't need any blob behavior.
/// All members are <c>virtual</c> so callers can override only the methods they care about
/// (see <see cref="RecordingBlobService"/> for an example).
/// </summary>
public class StubBlobService : IBlobStorageService
{
    public virtual Task InitializeAsync() => Task.CompletedTask;

    public virtual Task<string?> LoadFeedsConfigAsync() => Task.FromResult<string?>(null);

    public virtual Task SaveFeedsConfigAsync(string feedsJson) => Task.CompletedTask;

    public virtual Task<string?> LoadUsersConfigAsync() => Task.FromResult<string?>(null);

    public virtual Task SaveUsersConfigAsync(string usersJson) => Task.CompletedTask;

    public virtual Task UploadAudioAsync(string feedId, string fileName, string filePath) => Task.CompletedTask;

    public virtual Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath) => Task.CompletedTask;

    public virtual Task<Stream> DownloadAudioAsync(string feedId, string fileName) => Task.FromResult<Stream>(Stream.Null);

    public virtual Task<bool> AudioExistsAsync(string feedId, string fileName) => Task.FromResult(false);

    public virtual Task DeleteAudioAsync(string feedId, string fileName) => Task.CompletedTask;

    public virtual Task<List<string>> ListAudioFilesAsync(string feedId) => Task.FromResult<List<string>>([]);

    public virtual Task<long> GetAudioFileSizeAsync(string feedId, string fileName) => Task.FromResult(0L);

    public virtual Task<string> DownloadAudioToTempAsync(string feedId, string fileName) => Task.FromResult(string.Empty);

    public virtual Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length) => Task.FromResult<Stream>(Stream.Null);

    public virtual Task UploadIconAsync(string feedId, string filePath) => Task.CompletedTask;

    public virtual Task<string?> GetIconETagAsync(string feedId) => Task.FromResult<string?>(null);

    public virtual Task<Stream> DownloadIconAsync(string feedId) => Task.FromResult<Stream>(Stream.Null);

    public virtual Task DeleteIconAsync(string feedId) => Task.CompletedTask;

    public virtual Task SaveEpisodeMetadataAsync(string feedId, string metadataJson) => Task.CompletedTask;

    public virtual Task<string?> LoadEpisodeMetadataAsync(string feedId) => Task.FromResult<string?>(null);

    public virtual Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName) => Task.FromResult<Stream>(Stream.Null);

    public virtual Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName) => Task.FromResult(string.Empty);

    public virtual Task DeletePendingJobBlobsAsync(string feedId, string jobId) => Task.CompletedTask;

    public virtual Task<string?> LoadPushSubscriptionsAsync(string feedId) => Task.FromResult<string?>(null);

    public virtual Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson) => Task.CompletedTask;

    public virtual Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent) => Task.CompletedTask;

    public virtual Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId) => Task.FromResult<Stream?>(null);

    public virtual Task DeleteTranscriptAsync(string feedId, string episodeId) => Task.CompletedTask;

    public virtual Task RenameFeedAsync(string oldFeedId, string newFeedId) => Task.CompletedTask;

    public virtual Task DeleteFeedAsync(string feedId) => Task.CompletedTask;
}
