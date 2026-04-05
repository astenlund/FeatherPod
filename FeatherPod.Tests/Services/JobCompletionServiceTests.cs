using System.Threading.Channels;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Tests for the JobCompletionService join logic.
/// Uses an in-memory IJobService stub. Verifies entity state transitions
/// rather than intercepting episode creation (EpisodeService is sealed).
/// </summary>
[Collection("Sequential")]
public class JobCompletionServiceTests
{
    private readonly InMemoryJobService _jobService = new();
    private readonly JobCompletionService _service;

    public JobCompletionServiceTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "jcs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var blobService = new TestBlobStorageService(tempDir);
        var episodeService = new EpisodeService(blobService, NullLogger<EpisodeService>.Instance);

        _service = new JobCompletionService(
            _jobService,
            episodeService,
            blobService,
            new NullJobProgressChannel(),
            new NullFeedEventChannel(),
            new StubPushNotificationService(),
            NullLogger<JobCompletionService>.Instance);
    }

    [Fact]
    public async Task NormalizationCompletesFirst_TranscriptionStillRunning_NoEpisodeYet()
    {
        // Arrange
        var jobId = "job-norm-first";
        CreateTestEntity(jobId, transcriptionStatus: "Running");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 1024,
            AudioDurationMs = 60000
        }, CancellationToken.None);

        // Assert - job still Processing (transcription not done)
        var entity = _jobService.GetEntity(jobId)!;
        Assert.True(entity.NormalizationComplete);
        Assert.Equal(nameof(JobStatus.Processing), entity.Status);
        Assert.Null(entity.EpisodeId);
    }

    [Fact]
    public async Task BothTracksComplete_EpisodeCreated()
    {
        // Arrange
        var jobId = "job-both-done";
        CreateTestEntity(jobId, transcriptionStatus: "Completed");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 2048,
            AudioDurationMs = 120000
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
        Assert.NotNull(entity.CompletedAt);
    }

    [Fact]
    public async Task TranscriptionDisabled_NormalizationAlone_CreatesEpisode()
    {
        // Arrange
        var jobId = "job-no-trans";
        CreateTestEntity(jobId, transcriptionStatus: null);

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 512,
            AudioDurationMs = 30000
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
    }

    [Fact]
    public async Task NormalizationFails_JobMarkedFailed()
    {
        // Arrange
        var jobId = "job-norm-fail";
        CreateTestEntity(jobId, transcriptionStatus: null);

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = false,
            Error = "FFmpeg crashed"
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Failed), entity.Status);
        Assert.Null(entity.EpisodeId);
        Assert.Equal("FFmpeg crashed", entity.NormalizationError);
    }

    [Fact]
    public async Task TranscriptionFails_EpisodeStillCreated()
    {
        // Arrange
        var jobId = "job-trans-fail";
        CreateTestEntity(jobId, transcriptionStatus: "Failed");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 1024,
            AudioDurationMs = 60000
        }, CancellationToken.None);

        // Assert - episode created (normalization succeeded, transcription failure doesn't block)
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
    }

    private void CreateTestEntity(string jobId, string? transcriptionStatus)
    {
        _jobService.SetEntity(new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = jobId,
            Status = nameof(JobStatus.Processing),
            FeedId = "test-feed",
            FileName = "test.mp3",
            Title = "Test Episode",
            Description = "Test description",
            Source = "Browser",
            OriginalFileSize = 5000,
            TranscriptionStatus = transcriptionStatus,
            QueuedAt = DateTimeOffset.UtcNow
        });
    }

    // --- Stubs ---

    private sealed class InMemoryJobService : IJobService
    {
        private readonly Dictionary<string, JobStatusEntity> _entities = new();

        public void SetEntity(JobStatusEntity entity) => _entities[entity.RowKey] = entity;

        public JobStatusEntity? GetEntity(string jobId) => _entities.GetValueOrDefault(jobId);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_entities.GetValueOrDefault(jobId));
        }

        public Task CreateJobStatusAsync(string jobId, string feedId, string? fileName = null, string? title = null, string? progressMode = null, int? progressIntervalMs = null, string? description = null, string? summary = null, DateTimeOffset? publishedDate = null, string? source = null, long? originalFileSize = null, string? episodeId = null, string? transcriptionStatus = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);

        public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);

        public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);

        public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            mutate(entity);

            return Task.FromResult<JobStatusEntity?>(entity);
        }

        public Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            configure(entity);

            return Task.FromResult<JobStatusEntity?>(entity);
        }

        public Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                throw new Azure.RequestFailedException(404, "Not found");
            }

            configure(entity);

            return Task.CompletedTask;
        }
    }

    private sealed class NullJobProgressChannel : IJobProgressChannel
    {
        public void Publish(string jobId, JobStatusResponse response) { }

        public ChannelReader<JobStatusResponse> Subscribe(string jobId) => Channel.CreateUnbounded<JobStatusResponse>().Reader;

        public void Unsubscribe(string jobId, ChannelReader<JobStatusResponse> reader) { }
    }

    private sealed class NullFeedEventChannel : IFeedEventChannel
    {
        public void Publish(string feedId, string eventType) { }

        public ChannelReader<string> Subscribe(string feedId) => Channel.CreateUnbounded<string>().Reader;

        public void Unsubscribe(string feedId, ChannelReader<string> reader) { }
    }

    private sealed class StubPushNotificationService : PushNotificationService
    {
        public StubPushNotificationService() : base(
            new StubBlobService(),
            new EpisodeService(new StubBlobService(), NullLogger<EpisodeService>.Instance),
            new InMemoryJobService(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<PushNotificationService>.Instance)
        {
        }
    }

    private sealed class StubBlobService : IBlobStorageService
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<string?> LoadFeedsConfigAsync() => Task.FromResult<string?>(null);
        public Task SaveFeedsConfigAsync(string feedsJson) => Task.CompletedTask;
        public Task<string?> LoadUsersConfigAsync() => Task.FromResult<string?>(null);
        public Task SaveUsersConfigAsync(string usersJson) => Task.CompletedTask;
        public Task UploadAudioAsync(string feedId, string fileName, string filePath) => Task.CompletedTask;
        public Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath) => Task.CompletedTask;
        public Task<Stream> DownloadAudioAsync(string feedId, string fileName) => Task.FromResult<Stream>(Stream.Null);
        public Task<bool> AudioExistsAsync(string feedId, string fileName) => Task.FromResult(false);
        public Task DeleteAudioAsync(string feedId, string fileName) => Task.CompletedTask;
        public Task<List<string>> ListAudioFilesAsync(string feedId) => Task.FromResult<List<string>>([]);
        public Task<long> GetAudioFileSizeAsync(string feedId, string fileName) => Task.FromResult(0L);
        public Task<string> DownloadAudioToTempAsync(string feedId, string fileName) => Task.FromResult(string.Empty);
        public Task<Stream> DownloadAudioRangeAsync(string feedId, string fileName, long offset, long length) => Task.FromResult<Stream>(Stream.Null);
        public Task UploadIconAsync(string feedId, string filePath) => Task.CompletedTask;
        public Task<string?> GetIconETagAsync(string feedId) => Task.FromResult<string?>(null);
        public Task<Stream> DownloadIconAsync(string feedId) => Task.FromResult<Stream>(Stream.Null);
        public Task DeleteIconAsync(string feedId) => Task.CompletedTask;
        public Task SaveEpisodeMetadataAsync(string feedId, string metadataJson) => Task.CompletedTask;
        public Task<string?> LoadEpisodeMetadataAsync(string feedId) => Task.FromResult<string?>(null);
        public Task DeletePendingJobBlobsAsync(string feedId, string jobId) => Task.CompletedTask;
        public Task<string?> LoadPushSubscriptionsAsync(string feedId) => Task.FromResult<string?>(null);
        public Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson) => Task.CompletedTask;
        public Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent) => Task.CompletedTask;
        public Task<Stream?> DownloadTranscriptAsync(string feedId, string episodeId) => Task.FromResult<Stream?>(null);
        public Task DeleteTranscriptAsync(string feedId, string episodeId) => Task.CompletedTask;
        public Task RenameFeedAsync(string oldFeedId, string newFeedId) => Task.CompletedTask;
        public Task DeleteFeedAsync(string feedId) => Task.CompletedTask;
    }
}
