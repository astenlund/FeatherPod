using System.Reflection;
using System.Text.Json;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

[Collection("Sequential")]
public sealed class TranscriptRetryBoundaryTests
{
    private const string FeedId = "probe-feed";
    private const string EpisodeId = "probe-episode";

    [Fact]
    public async Task DelayedConcurrentRequest_DoesNotReuseEligibilityFromBeforePublication()
    {
        // Arrange
        var blob = new ProbeBlob();
        using var episodes = new EpisodeService(blob, NullLogger<EpisodeService>.Instance);
        var jobs = new InMemoryJobService();
        var (completion, retry) = Build(episodes, blob, jobs);
        await SeedAsync(episodes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        blob.NextAudioCheck = () =>
        {
            entered.TrySetResult();

            return release.Task;
        };
        var delayedRequest = retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        try
        {
            var first = await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);
            Assert.Equal(JobRetryOutcome.Accepted, first.Outcome);
            await blob.UploadPendingTranscriptAsync(FeedId, first.Job!.JobId, 1, "WEBVTT");
            await jobs.MergeJobFieldsAsync(first.Job.JobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, expectedAttempt: 1);
            await completion.TryCompleteJobAsync(first.Job.JobId, CancellationToken.None);
            Assert.Equal(TranscriptStatus.Available, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
            Assert.Equal(nameof(JobStatus.Completed), jobs.GetEntity(first.Job.JobId)!.Status);
        }
        finally
        {
            release.TrySetResult(true);
        }
        var delayedResult = await delayedRequest.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(delayedResult.Outcome != JobRetryOutcome.Accepted, $"A request that read Failed before another retry published was accepted after the episode became Available: {delayedResult.Outcome}");
    }

    [Fact]
    public async Task MetadataSaveFailure_DoesNotLeaveAvailableCacheWithFailedJob()
    {
        // Arrange
        var blob = new ProbeBlob();
        using var episodes = new EpisodeService(blob, NullLogger<EpisodeService>.Instance);
        var jobs = new InMemoryJobService();
        var (completion, retry) = Build(episodes, blob, jobs);
        await SeedAsync(episodes);
        var accepted = await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);
        Assert.Equal(JobRetryOutcome.Accepted, accepted.Outcome);
        var id = accepted.Job!.JobId;
        await blob.UploadPendingTranscriptAsync(FeedId, id, 1, "WEBVTT");
        await jobs.MergeJobFieldsAsync(id, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, expectedAttempt: 1);
        blob.FailNextMetadataSave = true;

        // Act
        await completion.TryCompleteJobAsync(id, CancellationToken.None);

        // Assert
        Assert.Equal(1, blob.FailedMetadataSaves);
        Assert.Equal(nameof(JobStatus.Completed), jobs.GetEntity(id)!.Status);
        Assert.Equal(TranscriptionStatuses.Failed, jobs.GetEntity(id)!.TranscriptionStatus);
        Assert.Equal(JobCompletionService.TranscriptNotPublishedError, jobs.GetEntity(id)!.TranscriptionError);
        var persisted = Assert.Single(JsonSerializer.Deserialize<List<Episode>>(blob.Metadata[FeedId])!);
        Assert.Equal(TranscriptStatus.Failed, persisted.TranscriptStatus);
        var cached = (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!;
        Assert.True(cached.TranscriptStatus == TranscriptStatus.Failed, $"Metadata write failed and the job reports Failed, but cached episode status is {cached.TranscriptStatus}");
    }

    [Fact]
    public async Task LateRunDuringStalledJoin_CannotTurnFailedOutcomeIntoUnpublishedSuccess()
    {
        // Arrange
        var blob = new ProbeBlob();
        using var episodes = new EpisodeService(blob, NullLogger<EpisodeService>.Instance);
        var storage = new InMemoryJobService();
        var jobs = DispatchProxy.Create<IJobService, SnapshotJobsProxy>();
        var proxy = (SnapshotJobsProxy)(object)jobs;
        proxy.Target = storage;
        var (completion, retry) = Build(episodes, blob, jobs);
        await SeedAsync(episodes);
        var accepted = await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);
        Assert.Equal(JobRetryOutcome.Accepted, accepted.Outcome);
        var id = accepted.Job!.JobId;
        await jobs.MergeJobFieldsAsync(id, e => e.TranscriptionStatus = TranscriptionStatuses.Running, expectedAttempt: 1);
        var lateWriteReached = false;
        proxy.AfterClaim = claimedId =>
        {
            Assert.Equal(id, claimedId);
            Assert.NotNull(storage.GetEntity(id)!.JoinClaimedAt);
            Assert.Equal(JobRetryService.TranscriptRetryStalledError, storage.GetEntity(id)!.TranscriptionError);
            Assert.Equal(TranscriptionStatuses.Failed, storage.GetEntity(id)!.TranscriptionStatus);
            blob.UploadPendingTranscriptAsync(FeedId, id, 1, "WEBVTT").GetAwaiter().GetResult();
            var late = jobs.MergeJobFieldsAsync(id, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, expectedAttempt: 1).GetAwaiter().GetResult();
            Assert.NotNull(late);
            completion.TryCompleteJobAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            lateWriteReached = true;
        };

        // Act
        await retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);

        // Assert
        Assert.True(lateWriteReached, "The fixture did not reach the late-result write after recovery claimed the failed retry");
        Assert.Equal(nameof(JobStatus.Completed), storage.GetEntity(id)!.Status);
        Assert.Equal(TranscriptStatus.Failed, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        Assert.Null(blob.CanonicalTranscript);
        Assert.Equal(TranscriptionStatuses.Failed, storage.GetEntity(id)!.TranscriptionStatus);
    }

    private static (JobCompletionService Completion, JobRetryService Retry) Build(EpisodeService episodes, IBlobStorageService blob, IJobService jobs)
    {
        var push = new StubPushNotificationService(new StubBlobService(), episodes, jobs);
        var completion = new JobCompletionService(jobs, episodes, blob, new NullJobProgressChannel(), new NullFeedEventChannel(), push, NullLogger<JobCompletionService>.Instance);
        var retry = new JobRetryService(jobs, episodes, blob, new TranscriptionChannel(), new TranscriptionTestHarness.FakeSpeechTranscriptionService(), new NullJobProgressChannel(), completion, push, new ConfigurationBuilder().Build(), NullLogger<JobRetryService>.Instance);

        return (completion, retry);
    }

    private static async Task SeedAsync(EpisodeService episodes)
    {
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Probe", Description = "d", Author = "a", Email = "a@example.com", Language = "en", Category = "Technology" });
        await episodes.AddEpisodeFromEntityAsync(new Episode
        {
            Id = EpisodeId,
            FeedId = FeedId,
            Title = "Probe episode",
            FileName = "probe.mp3",
            FileSize = 3,
            Duration = TimeSpan.FromMinutes(1),
            PublishedDate = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            Source = UploadSource.Browser,
            TranscriptStatus = TranscriptStatus.Failed,
        });
    }

    private sealed class ProbeBlob : StubBlobService
    {
        private readonly Dictionary<string, string> _staged = new();

        public Dictionary<string, string> Metadata { get; } = new();
        public Func<Task<bool>>? NextAudioCheck { get; set; }
        public bool FailNextMetadataSave { get; set; }
        public int FailedMetadataSaves { get; private set; }
        public string? CanonicalTranscript { get; private set; }

        public override Task<bool> AudioExistsAsync(string feedId, string fileName)
        {
            var next = NextAudioCheck;
            NextAudioCheck = null;

            return next?.Invoke() ?? Task.FromResult(true);
        }

        public override Task SaveEpisodeMetadataAsync(string feedId, string metadataJson)
        {
            if (FailNextMetadataSave)
            {
                FailNextMetadataSave = false;
                FailedMetadataSaves++;
                throw new IOException("Fixture: episode metadata storage unavailable");
            }
            Metadata[feedId] = metadataJson;

            return Task.CompletedTask;
        }

        public override Task UploadPendingTranscriptAsync(string feedId, string jobId, int attempt, string vttContent)
        {
            _staged[$"{feedId}/{jobId}/{attempt}"] = vttContent;

            return Task.CompletedTask;
        }

        public override Task<string?> DownloadPendingTranscriptAsync(string feedId, string jobId, int attempt)
        {
            return Task.FromResult(_staged.GetValueOrDefault($"{feedId}/{jobId}/{attempt}"));
        }

        public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
        {
            CanonicalTranscript = vttContent;

            return Task.CompletedTask;
        }
    }

    public class SnapshotJobsProxy : DispatchProxy
    {
        public InMemoryJobService Target { get; set; } = null!;
        public Action<string>? AfterClaim { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            var result = method.Invoke(Target, args);
            if (method.Name == nameof(IJobService.MergeWithETagAsync))
            {
                var marker = new JobStatusEntity();
                ((Action<JobStatusEntity>)args![1]!)(marker);
                if (marker.JoinClaimedAt != null && AfterClaim is { } callback)
                {
                    AfterClaim = null;

                    return AfterClaimAsync((Task)result!, (string)args[0]!, callback);
                }
            }
            if (result is Task<JobStatusEntity?> entityTask)
            {
                return CloneReadAsync(entityTask);
            }
            if (result is Task<List<JobStatusEntity>> listTask)
            {
                return CloneListAsync(listTask);
            }

            return result;
        }

        private static async Task AfterClaimAsync(Task write, string id, Action<string> callback)
        {
            await write;
            callback(id);
        }

        private static JobStatusEntity Clone(JobStatusEntity entity)
        {
            return (JobStatusEntity)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(entity, null)!;
        }

        private static async Task<JobStatusEntity?> CloneReadAsync(Task<JobStatusEntity?> read)
        {
            var entity = await read;

            return entity == null ? null : Clone(entity);
        }

        private static async Task<List<JobStatusEntity>> CloneListAsync(Task<List<JobStatusEntity>> read)
        {
            var entities = await read;

            return entities.Select(Clone).ToList();
        }
    }
}
