using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Lib.Net.Http.WebPush;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

/// <summary>
/// Unit tests for PushNotificationService -- subscription lifecycle, notification gating and
/// send-time removal of subscriptions whose endpoint is not a known push service.
/// </summary>
[Collection("Sequential")]
public class PushNotificationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly TestBlobStorageService _blobStorage;
    private readonly EpisodeService _episodeService;
    private readonly PushNotificationService _service;

    private readonly RecordingPushHandler _pushHandler = new();

    private const string TestFeedId = "test-feed";
    private const string TestEndpoint = "https://fcm.googleapis.com/fcm/send/abc123";
    private const string ForeignEndpoint = "https://10.0.0.5/collect";

    public PushNotificationServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        Directory.CreateDirectory(Path.Combine(_testDirectory, TestFeedId));

        _blobStorage = new TestBlobStorageService(_testDirectory);
        _episodeService = new EpisodeService(_blobStorage, NullLogger<EpisodeService>.Instance);
        _service = CreateService(_blobStorage);
    }

    private PushNotificationService CreateService(TestBlobStorageService blobStorage)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PushNotifications:VapidPublicKey"] = "BG4rjTmWamQxHPYhkj9mLM-o8ZW__X_6iA4ij3LGaDigQNFBsdFupd9UOXWB4qUCFypclIGnQWmcHMy8Cxm2Qyc",
                ["PushNotifications:VapidPrivateKey"] = "VdWePkz6ShPCrqDTyMHAUhujzpyJHC0Vez90Xe4CIlw",
                ["PushNotifications:VapidSubject"] = "mailto:test@example.com",
                ["PushNotifications:ActivityWindowHours"] = "2",
            })
            .Build();

        return new PushNotificationService(
            blobStorage,
            _episodeService,
            new StubJobService(),
            config,
            NullLogger<PushNotificationService>.Instance,
            new PushServiceClient(new HttpClient(_pushHandler)));
    }

    [Fact]
    public async Task SubscribeAsync_ForeignEndpoint_ReturnsFalseAndStoresNothing()
    {
        // Act
        var accepted = await _service.SubscribeAsync(TestFeedId, new PushSubscriptionRequest
        {
            Endpoint = ForeignEndpoint,
            P256dh = "testP256dh",
            Auth = "testAuth",
        });

        // Assert
        Assert.False(accepted);
        Assert.Null(await _blobStorage.LoadPushSubscriptionsAsync(TestFeedId));
    }

    [Fact]
    public async Task SubscribeAsync_PushServiceEndpoint_ReturnsTrueAndStoresIt()
    {
        // Act
        var accepted = await _service.SubscribeAsync(TestFeedId, new PushSubscriptionRequest
        {
            Endpoint = TestEndpoint,
            P256dh = "testP256dh",
            Auth = "testAuth",
        });

        // Assert
        Assert.True(accepted);
        Assert.Contains(TestEndpoint, await _blobStorage.LoadPushSubscriptionsAsync(TestFeedId));
    }

    [Fact]
    public async Task TryNotifyAsync_StoredForeignEndpoint_SendsNothingToItAndRemovesIt()
    {
        // Arrange - a subscription stored before endpoints were validated sits beside a genuine one.
        var now = DateTime.UtcNow;
        var (p256dh, auth) = CreateSubscriptionKeys();
        List<PushSubscriptionInfo> stored =
        [
            new() { Endpoint = TestEndpoint, P256dh = p256dh, Auth = auth, CreatedAt = now, LastActiveAt = now },
            new() { Endpoint = ForeignEndpoint, P256dh = p256dh, Auth = auth, CreatedAt = now, LastActiveAt = now },
        ];
        await _blobStorage.SavePushSubscriptionsAsync(TestFeedId, JsonSerializer.Serialize(stored));

        // Act
        await _service.TryNotifyAsync(TestFeedId, "Upload complete", "Episode", "/test-feed/icon-192.png");

        // Assert - only the push service is contacted, and the foreign subscription is gone from storage.
        Assert.Equal([TestEndpoint], _pushHandler.RequestedUris);
        var afterNotify = await _blobStorage.LoadPushSubscriptionsAsync(TestFeedId);
        Assert.Contains(TestEndpoint, afterNotify!);
        Assert.DoesNotContain(ForeignEndpoint, afterNotify!);
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

    [Fact]
    public async Task TryNotifyAsync_RemovalCannotBePersisted_StillDeliversToGenuineSubscription()
    {
        // Arrange - the store that later refuses writes holds a foreign subscription beside a genuine one.
        var now = DateTime.UtcNow;
        var (p256dh, auth) = CreateSubscriptionKeys();
        List<PushSubscriptionInfo> stored =
        [
            new() { Endpoint = TestEndpoint, P256dh = p256dh, Auth = auth, CreatedAt = now, LastActiveAt = now },
            new() { Endpoint = ForeignEndpoint, P256dh = p256dh, Auth = auth, CreatedAt = now, LastActiveAt = now },
        ];
        var failingStorage = new FailingSaveBlobStorageService(_testDirectory);
        await _blobStorage.SavePushSubscriptionsAsync(TestFeedId, JsonSerializer.Serialize(stored));
        var service = CreateService(failingStorage);

        // Act
        await service.TryNotifyAsync(TestFeedId, "Upload complete", "Episode", "/test-feed/icon-192.png");

        // Assert - the failed save of the removal does not stop delivery to the push service.
        Assert.Equal(1, failingStorage.FailedSaves);
        Assert.Equal([TestEndpoint], _pushHandler.RequestedUris);
    }

    private static (string P256dh, string Auth) CreateSubscriptionKeys()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = ecdh.ExportParameters(false).Q;
        byte[] publicKey = [0x04, .. point.X!, .. point.Y!];

        return (Base64Url.EncodeToString(publicKey), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    /// <summary>
    /// Reads like the file-based store but fails every subscription save, as a storage outage would.
    /// </summary>
    private sealed class FailingSaveBlobStorageService(string rootPath) : TestBlobStorageService(rootPath)
    {
        public int FailedSaves { get; private set; }

        public override Task SavePushSubscriptionsAsync(string feedId, string subscriptionsJson)
        {
            FailedSaves++;

            throw new IOException("Simulated storage failure");
        }
    }

    /// <summary>
    /// Records every push delivery request and answers 201 Created, as a push service does.
    /// </summary>
    private sealed class RecordingPushHandler : HttpMessageHandler
    {
        private readonly List<string> _requestedUris = [];

        public IReadOnlyList<string> RequestedUris
        {
            get
            {
                lock (_requestedUris)
                {
                    return [.. _requestedUris];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requestedUris)
            {
                _requestedUris.Add(request.RequestUri!.ToString());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
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
        public Task CreateJobStatusAsync(CreateJobOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);
        public Task<List<JobStatusEntity>> GetActiveJobsByTranscriptionStatusAsync(IReadOnlyCollection<string> transcriptionStatuses, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);

        public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);
        public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
        public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
        public Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);
        public Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
