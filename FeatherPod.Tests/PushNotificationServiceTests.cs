using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

/// <summary>
/// Unit tests for PushNotificationService -- subscription lifecycle and notification gating.
/// </summary>
[Collection("Sequential")]
public class PushNotificationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly TestBlobStorageService _blobStorage;
    private readonly EpisodeService _episodeService;
    private readonly PushNotificationService _service;

    private const string TestFeedId = "test-feed";
    private const string TestEndpoint = "https://push.example.com/sub/abc123";

    public PushNotificationServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        Directory.CreateDirectory(Path.Combine(_testDirectory, TestFeedId));

        _blobStorage = new TestBlobStorageService(_testDirectory);
        _episodeService = new EpisodeService(_blobStorage, NullLogger<EpisodeService>.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PushNotifications:VapidPublicKey"] = "BG4rjTmWamQxHPYhkj9mLM-o8ZW__X_6iA4ij3LGaDigQNFBsdFupd9UOXWB4qUCFypclIGnQWmcHMy8Cxm2Qyc",
                ["PushNotifications:VapidPrivateKey"] = "VdWePkz6ShPCrqDTyMHAUhujzpyJHC0Vez90Xe4CIlw",
                ["PushNotifications:VapidSubject"] = "mailto:test@example.com",
                ["PushNotifications:ActivityWindowHours"] = "2",
            })
            .Build();

        _service = new PushNotificationService(
            _blobStorage,
            _episodeService,
            new StubJobService(),
            config,
            NullLogger<PushNotificationService>.Instance);
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesSubscription_PreventsStaleNotifications()
    {
        // Arrange - subscribe
        await _service.SubscribeAsync(TestFeedId, new PushSubscriptionRequest
        {
            Endpoint = TestEndpoint,
            P256dh = "testP256dh",
            Auth = "testAuth",
        });

        var afterSubscribe = await _blobStorage.LoadPushSubscriptionsAsync(TestFeedId);
        Assert.Contains(TestEndpoint, afterSubscribe!);

        // Act - unsubscribe (this is what deleteServerSubscription() triggers)
        await _service.UnsubscribeAsync(TestFeedId, TestEndpoint);

        // Assert - subscription removed from storage
        var afterUnsubscribe = await _blobStorage.LoadPushSubscriptionsAsync(TestFeedId);
        Assert.DoesNotContain(TestEndpoint, afterUnsubscribe!);
    }

    [Fact]
    public async Task UnsubscribeAsync_CleansUpSession_WhenNoSubscriptionsRemain()
    {
        // Arrange - subscribe, then create a session
        await _service.SubscribeAsync(TestFeedId, new PushSubscriptionRequest
        {
            Endpoint = TestEndpoint,
            P256dh = "testP256dh",
            Auth = "testAuth",
        });
        await _service.TrackJobsAsync(TestFeedId, ["job-1"]);

        // Act - unsubscribe (last subscription removed)
        await _service.UnsubscribeAsync(TestFeedId, TestEndpoint);

        // Assert - TryNotifyJobTerminal should take the fallback path (no session),
        // and the fallback should skip (no active subscriptions). This should not throw.
        var progress = new JobStatusResponse
        {
            JobId = "job-1",
            FeedId = TestFeedId,
            Status = nameof(JobStatus.Completed),
            FileName = "test-episode.m4a",
        };
        var exception = Record.Exception(() => _service.TryNotifyJobTerminal(progress));
        Assert.Null(exception);
    }

    [Fact]
    public async Task TryNotifyJobTerminal_WithNoSubscriptions_DoesNotThrow()
    {
        // Arrange - no subscriptions registered at all
        var progress = new JobStatusResponse
        {
            JobId = "job-1",
            FeedId = TestFeedId,
            Status = nameof(JobStatus.Completed),
            FileName = "test-episode.m4a",
        };

        // Act & Assert
        var exception = Record.Exception(() => _service.TryNotifyJobTerminal(progress));
        Assert.Null(exception);
    }

    public void Dispose()
    {
        _episodeService.Dispose();

        try
        {
            Directory.Delete(_testDirectory, true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Minimal IJobService stub -- push notification tests don't need job queue/table operations.
    /// </summary>
    private sealed class StubJobService : IJobService
    {
        public Task InitializeAsync() => Task.CompletedTask;
        public Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
        public Task CreateJobStatusAsync(string jobId, string feedId, string? fileName = null, string? title = null, string? progressMode = null, int? progressIntervalMs = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);
        public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);
        public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
        public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
    }
}
