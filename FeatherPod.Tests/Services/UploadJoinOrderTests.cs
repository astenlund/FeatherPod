using System.Reflection;
using System.Threading.Channels;
using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// The upload join claims its job without ending it, publishes, and only then writes the single terminal status that matches what it
/// published; the recurring recovery ends a claimed join that stopped.
/// </summary>
[Collection("Sequential")]
public sealed class UploadJoinOrderTests : IDisposable
{
    private const string FeedId = "join-feed";
    private const string EpisodeId = "join-episode";
    private const string JobId = "join-job";

    private readonly JoinBlob _blob = new();
    private readonly InMemoryJobService _storage = new();
    private readonly IJobService _jobs;
    private readonly RecordingJobsProxy _proxy;
    private readonly EpisodeService _episodes;
    private readonly RecordingProgressChannel _progress = new();
    private readonly JobCompletionService _completion;
    private readonly JobRetryService _retry;

    public UploadJoinOrderTests()
    {
        _jobs = DispatchProxy.Create<IJobService, RecordingJobsProxy>();
        _proxy = (RecordingJobsProxy)(object)_jobs;
        _proxy.Target = _storage;
        _episodes = new EpisodeService(_blob, NullLogger<EpisodeService>.Instance);
        var push = new StubPushNotificationService(new StubBlobService(), _episodes, _jobs);
        _completion = new JobCompletionService(_jobs, _episodes, _blob, _progress, new NullFeedEventChannel(), push, NullLogger<JobCompletionService>.Instance);
        _retry = new JobRetryService(_jobs, _episodes, _blob, new TranscriptionChannel(), new TranscriptionTestHarness.FakeSpeechTranscriptionService(), new NullJobProgressChannel(), _completion, push, new ConfigurationBuilder().Build(), NullLogger<JobRetryService>.Instance);
    }

    /// <summary>A recovery time past the bound after which a claimed upload join counts as stopped.</summary>
    private static DateTimeOffset PastTheClaimBound => DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>A recovery time past the bound after which a claimed upload join whose publish is not found counts as not published.</summary>
    private static DateTimeOffset PastTheUnlandedBound => DateTimeOffset.UtcNow.AddHours(7);

    public void Dispose() => _episodes.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_FreshClaimWithoutALiveJoinInThisProcess_LeavesTheJobActive(bool published)
    {
        // Arrange - a claim a minute old whose join runs elsewhere (an overlapped restart's old process), which may already have
        // published (only the claim's age then keeps recovery from ending the job) or not
        var incarnation = DateTime.UtcNow.AddMinutes(-1);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
        if (published)
        {
            await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation));
        }

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.False(job.GetJobStatus().IsTerminal(), $"Recovery ended a fresh claim as {job.Status}");
        Assert.Equal(EpisodeId, job.EpisodeId);
        Assert.Empty(_proxy.StatusWrites);
    }

    [Fact]
    public async Task UploadJoin_PublishesTheEpisodeBeforeItWritesTheTerminalStatus()
    {
        // Arrange
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        JobStatusEntity? atPublish = null;
        _blob.OnMetadataSave = () => atPublish = Snapshot(_storage.GetEntity(JobId)!);

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.NotNull(atPublish);
        Assert.False(atPublish.GetJobStatus().IsTerminal(), $"The job read {atPublish.Status} while its episode was being published");
        Assert.NotNull(atPublish.JoinClaimedAt);
        var job = _storage.GetEntity(JobId)!;
        var episode = (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(EpisodeId, job.EpisodeId);
        Assert.Equal(TranscriptionStatuses.Completed, job.TranscriptionStatus);
        Assert.Equal(TranscriptStatus.Available, episode.TranscriptStatus);
        Assert.Equal(episode.UploadedAt.ToUniversalTime(), job.EpisodeUploadedAt!.Value.UtcDateTime);
        Assert.Single(_proxy.StatusWrites);
    }

    [Fact]
    public async Task UploadJoin_StagedTranscriptUnreadable_EndsCompletedWithAFailedTranscriptInOneStatusWrite()
    {
        // Arrange
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null);
        _blob.PendingTranscriptThrows = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Equal(JobCompletionService.TranscriptNotPublishedError, job.TranscriptionError);
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        var write = Assert.Single(_proxy.StatusWrites);
        Assert.Equal(TranscriptionStatuses.Failed, write.TranscriptionStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadJoin_TranscriptStorageTimesOut_EndsCompletedWithAFailedTranscriptInOneStatusWrite(bool onRead)
    {
        // Arrange - the storage SDK surfaces a timeout as a TaskCanceledException tied to no caller token
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _blob.PendingTranscriptTimesOut = onRead;
        _blob.TranscriptWriteTimesOut = !onRead;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Equal(JobCompletionService.TranscriptNotPublishedError, job.TranscriptionError);
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        Assert.Single(_proxy.StatusWrites);
    }

    [Fact]
    public async Task UploadJoin_MetadataSaveFails_EndsFailedAndLeavesNoEpisodeInMemory()
    {
        // Arrange
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _blob.FailNextMetadataSave = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Failed), job.Status);
        Assert.Equal(JobCompletionService.EpisodeNotPublishedError, job.Error);
        Assert.Null(job.EpisodeId);
        Assert.Null(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
        var write = Assert.Single(_proxy.StatusWrites);
        Assert.Equal(nameof(JobStatus.Failed), write.Status);
        Assert.Null(write.EpisodeId);
    }

    [Fact]
    public async Task UploadJoin_CallerCancelledAfterTheClaim_StillPublishesAndCompletes()
    {
        // Arrange - the request that ran the join is aborted right after the claim
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        using var aborted = new CancellationTokenSource();
        _proxy.AfterClaimWrite = aborted.Cancel;

        // Act
        await _completion.TryCompleteJobAsync(JobId, aborted.Token);

        // Assert
        Assert.True(aborted.IsCancellationRequested, "The fixture did not reach the claim");
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
    }

    [Fact]
    public async Task UploadJoin_CallerCancelledWhileItsClaimWriteCommits_StillPublishesAndCompletes()
    {
        // Arrange - the table commits the claim, then the write observes the aborted request on the token it was given
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        using var aborted = new CancellationTokenSource();
        _proxy.CancelAfterClaimCommit = aborted;

        // Act
        await _completion.TryCompleteJobAsync(JobId, aborted.Token);

        // Assert
        Assert.True(aborted.IsCancellationRequested, "The fixture did not reach the claim");
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
    }

    [Fact]
    public async Task UploadJoin_ClaimWriteCommitsButReportsAConflict_StillPublishesAndCompletes()
    {
        // Arrange - the table commits the claim, the response is lost, and the SDK's replay with the old ETag answers 412
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _proxy.ConflictAfterClaimCommit = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.False(_proxy.ConflictAfterClaimCommit, "The fixture did not reach the claim");
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
    }

    [Fact]
    public async Task UploadJoin_EarlierClaimWriteCommitsAfterTheJoinRetried_StillPublishesAndCompletes()
    {
        // Arrange - the first claim write fails on the client while still pending, the join retries, the pending first write then commits
        // and the retry's conditional write answers 412
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _proxy.FirstClaimCommitsLate = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.False(_proxy.FirstClaimCommitsLate, "The fixture did not reach the second claim write");
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.NotNull(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UploadJoin_TerminalWriteCommitsButReportsAConflict_StillReportsTheTerminalStatus(bool metadataSaveFails, bool viaRecovery)
    {
        // Arrange - the terminal write commits, its response is lost, and the replay answers a conflict
        if (viaRecovery)
        {
            await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: DateTime.UtcNow.AddMinutes(-5));
        }
        else
        {
            await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
            _blob.FailNextMetadataSave = metadataSaveFails;
        }

        _proxy.ConflictAfterStatusCommit = true;

        // Act
        if (viaRecovery)
        {
            await _retry.RecoverStalledRetriesAsync(metadataSaveFails ? PastTheUnlandedBound : PastTheClaimBound, CancellationToken.None);
        }
        else
        {
            await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);
        }

        // Assert - the join went on to report the status, which deletes the job's pending blobs
        Assert.False(_proxy.ConflictAfterStatusCommit, "The fixture did not reach the terminal write");
        Assert.Equal(metadataSaveFails ? nameof(JobStatus.Failed) : nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Contains(JobId, _blob.PendingDeletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UploadJoin_TerminalWriteCommitsButFailsInTransport_StillReportsTheTerminalStatus(bool metadataSaveFails, bool viaRecovery)
    {
        // Arrange - the terminal write commits and its response is lost to a transport failure
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        if (viaRecovery)
        {
            await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
            if (!metadataSaveFails)
            {
                await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation));
            }
        }
        else
        {
            await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
            _blob.FailNextMetadataSave = metadataSaveFails;
        }

        _proxy.TransportFailureAfterStatusCommit = true;

        // Act
        if (viaRecovery)
        {
            await _retry.RecoverStalledRetriesAsync(metadataSaveFails ? PastTheUnlandedBound : PastTheClaimBound, CancellationToken.None);
        }
        else
        {
            await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);
        }

        // Assert
        Assert.False(_proxy.TransportFailureAfterStatusCommit, "The fixture did not reach the terminal write");
        var expected = metadataSaveFails ? nameof(JobStatus.Failed) : nameof(JobStatus.Completed);
        Assert.Equal(expected, _storage.GetEntity(JobId)!.Status);
        var reported = Assert.Single(_progress.Published, p => p.JobId == JobId);
        Assert.Equal(expected, reported.Response.Status);
        Assert.Contains(JobId, _blob.PendingDeletes);
    }

    [Fact]
    public async Task UploadJoin_AnotherJoinsClaimWithTheSameStamp_IsNotTakenForItsOwn()
    {
        // Arrange - another process's join claims the job in the same millisecond, just before this join's claim write
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _proxy.ForeignClaimWithTheSameStamp = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert - the other join owns the job; this one publishes and writes nothing
        Assert.False(_proxy.ForeignClaimWithTheSameStamp, "The fixture did not reach the claim");
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(RecordingJobsProxy.ForeignJoinWriteToken, job.JoinWriteToken);
        Assert.False(job.GetJobStatus().IsTerminal());
        Assert.Null(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
        Assert.Empty(_proxy.StatusWrites);
    }

    [Fact]
    public async Task UploadJoin_DeferredTitleSavedWhileClaimed_ReachesTheEpisodeEvenWhenNoJobReadSucceedsAfterItsTerminalWrite()
    {
        // Arrange - the deferred title lands while the episode is being published; every read after the terminal write fails
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _blob.OnMetadataSave = () => Assert.True(_completion.ApplyDeferredTitleAsync(JobId, "Deferred title", "Placeholder", CancellationToken.None).GetAwaiter().GetResult());
        _proxy.FailReadsAfterStatusWrite = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Equal("Deferred title", (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Fact]
    public async Task UploadJoin_JobTableUnreadableAfterTheTerminalWrite_StillReportsTheTerminalStatus()
    {
        // Arrange - every job read after the terminal write fails
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _proxy.FailReadsAfterStatusWrite = true;

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        var reported = Assert.Single(_progress.Published, p => p.JobId == JobId);
        Assert.Equal(nameof(JobStatus.Completed), reported.Response.Status);
        Assert.Equal(EpisodeId, reported.Response.EpisodeId);
        Assert.Contains(JobId, _blob.PendingDeletes);
    }

    [Fact]
    public async Task Recovery_JobTableUnreadableAfterItEndsAStoppedJoin_StillReportsTheTerminalStatus()
    {
        // Arrange - a join published and stopped while claimed; every job read after recovery's terminal write fails
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation));
        _proxy.FailReadsAfterStatusWrite = true;

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        var reported = Assert.Single(_progress.Published, p => p.JobId == JobId);
        Assert.Equal(nameof(JobStatus.Completed), reported.Response.Status);
    }

    [Fact]
    public async Task Recovery_StoppedUploadJoin_KeepsATitleTheUserSavedOnTheEpisode()
    {
        // Arrange - a join published under the job's explicit title and stopped; the user then renamed the episode
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation, title: "Explicit title");
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { Title = "User edit" });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Equal("User edit", (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Fact]
    public async Task Recovery_StoppedUploadJoinWithADeferredTitle_RenamesTheEpisodeFromItsPlaceholder()
    {
        // Arrange - the join published under the filename placeholder; the deferred title reached only the job before it stopped
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        var placeholder = EpisodeService.ParseTitleFromFilename("join.mp3");
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation, title: "Deferred title", publishedTitle: placeholder);
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { Title = placeholder });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.Equal("Deferred title", (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Fact]
    public async Task Recovery_StoppedExplicitlyTitledJoin_KeepsAUserEditToTheFilenameTitle()
    {
        // Arrange - the join published under its explicit title and stopped; the user then saved the filename-derived title
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        var placeholder = EpisodeService.ParseTitleFromFilename("join.mp3");
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation, title: "Explicit title");
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { Title = placeholder });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Equal(placeholder, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Fact]
    public async Task Recovery_StoppedUploadJoinWhoseEpisodeWasMovedToAnotherFeed_EndsCompleted()
    {
        // Arrange - the join published, its terminal write failed, and the user moved the episode before recovery ran
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
        await _episodes.CreateFeedAsync(new FeedConfig { Id = "join-other", Title = "Other", Description = "d", Author = "a", Email = "a@example.com", Language = "en", Category = "Technology" });
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { Id = "moved-episode", FeedId = "join-other" });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Completed, job.TranscriptionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptRetry_OfAnEpisodeItsUploadStillHoldsClaimed_PublishesItsTranscript(bool uploadCompletesBeforeTheRetryJoins)
    {
        // Arrange - the upload published a failed transcript and is still claimed (or completes later) when the user retries it
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Failed, stagedTranscript: null, claimedFor: incarnation);
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { TranscriptStatus = TranscriptStatus.Failed });
        var accepted = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);
        Assert.Equal(JobRetryOutcome.Accepted, accepted.Outcome);
        var retryId = accepted.Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, retryId, 1, "WEBVTT\n\nretried");
        await _storage.MergeJobFieldsAsync(retryId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, expectedAttempt: 1);
        if (uploadCompletesBeforeTheRetryJoins)
        {
            await _storage.MergeJobFieldsAsync(JobId, e =>
            {
                e.Status = nameof(JobStatus.Completed);
                e.CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            });
        }

        // Act
        await _completion.TryCompleteJobAsync(retryId, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        Assert.Equal(TranscriptionStatuses.Completed, _storage.GetEntity(retryId)!.TranscriptionStatus);
    }

    [Fact]
    public async Task Recovery_StoppedUploadJoinWhoseEpisodeSurvivesOnlyAsACopy_LeavesTheCopysTitleAlone()
    {
        // Arrange - the join published under the placeholder and stopped; the episode was copied to another feed, then deleted
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        var placeholder = EpisodeService.ParseTitleFromFilename("join.mp3");
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation, title: "Deferred title", publishedTitle: placeholder);
        await _episodes.CreateFeedAsync(new FeedConfig { Id = "join-other", Title = "Other", Description = "d", Author = "a", Email = "a@example.com", Language = "en", Category = "Technology" });
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { Id = "copied-episode", FeedId = "join-other", Title = placeholder });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Equal(placeholder, (await _episodes.GetEpisodeByIdAsync("join-other", "copied-episode"))!.Title);
    }

    [Fact]
    public async Task UploadJoin_DeferredTitleWhoseRenameSaveFails_StaysClaimedUntilRecoveryCarriesTheTitle()
    {
        // Arrange - the deferred title lands while the episode is published, and saving its rename then fails once
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _blob.OnMetadataSave = () =>
        {
            Assert.True(_completion.ApplyDeferredTitleAsync(JobId, "Deferred title", "Placeholder", CancellationToken.None).GetAwaiter().GetResult());
            _blob.FailNextMetadataSave = true;
        };

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);
        var afterJoin = Snapshot(_storage.GetEntity(JobId)!);
        var cachedAfterJoin = (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title;
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        Assert.False(afterJoin.GetJobStatus().IsTerminal(), $"The join ended the job {afterJoin.Status} over a title it could not save");
        Assert.Equal("Placeholder", cachedAfterJoin);
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Equal("Deferred title", (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Fact]
    public async Task UploadJoin_RecoveryWhileTheJoinIsLive_LeavesTheJobToTheJoin()
    {
        // Arrange - recovery runs after the join published and before it writes the terminal status
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        var recoveryRan = false;
        _proxy.BeforeStatusWrite = () =>
        {
            _retry.RecoverStalledRetriesAsync(PastTheUnlandedBound, CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(_storage.GetEntity(JobId)!.GetJobStatus().IsTerminal(), "Recovery ended a join that was still running");
            recoveryRan = true;
        };

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.True(recoveryRan, "The fixture did not reach the terminal write");
        Assert.Equal(nameof(JobStatus.Completed), _storage.GetEntity(JobId)!.Status);
        Assert.Single(_proxy.StatusWrites);
    }

    [Fact]
    public async Task UploadJoin_DeferredTitleArrivingBeforeTheTerminalStatus_ReachesTheEpisode()
    {
        // Arrange - the deferred title lands after the publish, between the terminal write's read and the write, which then conflicts
        // as the table's ETag check makes it
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: "WEBVTT\n\njoin");
        _proxy.BeforeStatusWrite = () => _storage.BeforeNextETagMergeConflict = () =>
            Assert.True(_completion.ApplyDeferredTitleAsync(JobId, "Deferred title", "Placeholder", CancellationToken.None).GetAwaiter().GetResult());

        // Act
        await _completion.TryCompleteJobAsync(JobId, CancellationToken.None);

        // Assert
        Assert.Equal("Deferred title", (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.Title);
    }

    [Theory]
    [InlineData(TranscriptStatus.Available, TranscriptionStatuses.Completed, null)]
    [InlineData(TranscriptStatus.Failed, TranscriptionStatuses.Failed, JobCompletionService.TranscriptNotPublishedError)]
    public async Task Recovery_StoppedUploadJoinWhosePublishLanded_EndsCompletedWithTheEpisodesTranscriptOutcome(TranscriptStatus episodeTranscript, string expectedTrack, string? expectedError)
    {
        // Arrange - a join claimed the job and published, then the server stopped before the terminal write
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation) with { TranscriptStatus = episodeTranscript });

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(EpisodeId, job.EpisodeId);
        Assert.Equal(expectedTrack, job.TranscriptionStatus);
        Assert.Equal(expectedError, job.TranscriptionError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_StoppedUploadJoinWhosePublishDidNotLand_EndsFailed(bool otherIncarnationExists)
    {
        // Arrange - a join claimed the job and stopped before publishing; the episode id is absent or holds another incarnation
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);
        if (otherIncarnationExists)
        {
            await _episodes.AddEpisodeFromEntityAsync(NewEpisode(incarnation.AddMinutes(-60)));
        }

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheUnlandedBound, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.Equal(nameof(JobStatus.Failed), job.Status);
        Assert.Equal(JobCompletionService.EpisodeNotPublishedError, job.Error);
        Assert.Null(job.EpisodeId);
        var write = Assert.Single(_proxy.StatusWrites);
        Assert.Equal(nameof(JobStatus.Failed), write.Status);
        Assert.Null(write.EpisodeId);
    }

    [Fact]
    public async Task Recovery_StoppedUploadJoinNotFoundBeforeTheUnlandedBound_LeavesTheJobClaimed()
    {
        // Arrange - the claim is past the stopped-join bound, but this process does not see the incarnation: its join may still be
        // publishing in another process, or this process's episode cache may predate the publish
        var incarnation = DateTime.UtcNow.AddMinutes(-5);
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: incarnation);

        // Act
        await _retry.RecoverStalledRetriesAsync(PastTheClaimBound, CancellationToken.None);

        // Assert
        var job = _storage.GetEntity(JobId)!;
        Assert.False(job.GetJobStatus().IsTerminal(), $"Recovery ended an unlanded claim as {job.Status} before the unlanded bound");
        Assert.Equal(EpisodeId, job.EpisodeId);
        Assert.Empty(_proxy.StatusWrites);
    }

    [Fact]
    public async Task ClaimedUploadJob_IsNotAnUnjoinedRecordedJob()
    {
        // Arrange
        await SeedJobAsync(TranscriptionStatuses.Completed, stagedTranscript: null, claimedFor: DateTime.UtcNow);

        // Act
        var unjoined = JobRetryService.IsUnjoinedRecordedJob(_storage.GetEntity(JobId)!);

        // Assert
        Assert.False(unjoined);
    }

    private async Task SeedJobAsync(string transcriptionStatus, string? stagedTranscript, DateTime? claimedFor = null, string title = "Placeholder", string? publishedTitle = null)
    {
        await _episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Join", Description = "d", Author = "a", Email = "a@example.com", Language = "en", Category = "Technology" });
        if (stagedTranscript != null)
        {
            await _blob.UploadPendingTranscriptAsync(FeedId, JobId, 1, stagedTranscript);
        }

        _storage.SetEntity(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = JobId,
            Status = nameof(JobStatus.Processing),
            FeedId = FeedId,
            FileName = "join.mp3",
            EpisodeId = EpisodeId,
            Title = title,
            Source = "Browser",
            OriginalFileSize = 5000,
            NormalizedFileSize = 4000,
            NormalizationComplete = true,
            TranscriptionStatus = transcriptionStatus,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            JoinClaimedAt = claimedFor.HasValue ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            EpisodeUploadedAt = claimedFor.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(claimedFor.Value, DateTimeKind.Utc)) : null,
            PublishedTitle = claimedFor.HasValue ? publishedTitle ?? title : null,
        });
    }

    private static Episode NewEpisode(DateTime uploadedAt) => new()
    {
        Id = EpisodeId,
        FeedId = FeedId,
        Title = "Placeholder",
        FileName = "join.mp3",
        FileSize = 4000,
        Duration = TimeSpan.FromMinutes(1),
        PublishedDate = DateTime.UtcNow,
        UploadedAt = uploadedAt,
        Source = UploadSource.Browser,
        TranscriptStatus = TranscriptStatus.Available,
    };

    private static JobStatusEntity Snapshot(JobStatusEntity entity) =>
        (JobStatusEntity)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(entity, null)!;

    private sealed class JoinBlob : StubBlobService
    {
        private readonly Dictionary<string, string> _staged = new();

        public Action? OnMetadataSave { get; set; }

        public bool FailNextMetadataSave { get; set; }

        public bool PendingTranscriptThrows { get; set; }

        public override Task<bool> AudioExistsAsync(string feedId, string fileName) => Task.FromResult(true);

        public List<string> PendingDeletes { get; } = [];

        public override Task DeletePendingJobBlobsAsync(string feedId, string jobId)
        {
            PendingDeletes.Add(jobId);

            return Task.CompletedTask;
        }

        public override Task SaveEpisodeMetadataAsync(string feedId, string metadataJson)
        {
            if (FailNextMetadataSave)
            {
                FailNextMetadataSave = false;
                throw new IOException("Fixture: episode metadata storage unavailable");
            }

            OnMetadataSave?.Invoke();
            OnMetadataSave = null;

            return Task.CompletedTask;
        }

        public override Task UploadPendingTranscriptAsync(string feedId, string jobId, int attempt, string vttContent)
        {
            _staged[$"{feedId}/{jobId}/{attempt}"] = vttContent;

            return Task.CompletedTask;
        }

        public bool PendingTranscriptTimesOut { get; set; }

        public bool TranscriptWriteTimesOut { get; set; }

        public override Task<string?> DownloadPendingTranscriptAsync(string feedId, string jobId, int attempt) =>
            PendingTranscriptTimesOut ? throw new TaskCanceledException("Fixture: storage request timed out")
            : PendingTranscriptThrows ? throw new IOException("Fixture: staged transcript unreadable")
            : Task.FromResult(_staged.GetValueOrDefault($"{feedId}/{jobId}/{attempt}"));

        public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent) =>
            TranscriptWriteTimesOut ? throw new TaskCanceledException("Fixture: storage request timed out") : Task.CompletedTask;
    }

    /// <summary>Records every progress update the join publishes.</summary>
    private sealed class RecordingProgressChannel : IJobProgressChannel
    {
        public List<(string JobId, JobStatusResponse Response)> Published { get; } = [];

        public void Publish(string jobId, JobStatusResponse response) => Published.Add((jobId, response));

        public ChannelReader<JobStatusResponse> Subscribe(string jobId) => Channel.CreateUnbounded<JobStatusResponse>().Reader;

        public void Unsubscribe(string jobId, ChannelReader<JobStatusResponse> reader)
        {
        }
    }

    /// <summary>
    /// Forwards to an <see cref="InMemoryJobService"/>, recording every ETag merge or replace that writes a job status (the terminal
    /// writes). Runs <see cref="BeforeStatusWrite"/> once before the first of them and <see cref="AfterClaimWrite"/> once after the join's
    /// claim, and with <see cref="FailReadsAfterStatusWrite"/> fails every job read after a status write (a join stopping there).
    /// </summary>
    public class RecordingJobsProxy : DispatchProxy
    {
        public InMemoryJobService Target { get; set; } = null!;

        public Action? BeforeStatusWrite { get; set; }

        public Action? AfterClaimWrite { get; set; }

        public bool FailReadsAfterStatusWrite { get; set; }

        /// <summary>When set, the claim write commits, then this source is cancelled and the write observes the token it was passed.</summary>
        public CancellationTokenSource? CancelAfterClaimCommit { get; set; }

        /// <summary>When set, the claim write commits and then fails with 412, as an SDK replay of a committed write does (clears itself).</summary>
        public bool ConflictAfterClaimCommit { get; set; }

        /// <summary>When set, the first status write commits and then reports a conflict, as an SDK replay of a committed write does (clears itself).</summary>
        public bool ConflictAfterStatusCommit { get; set; }

        /// <summary>When set, the first status write commits and then fails at the transport, its response lost (clears itself).</summary>
        public bool TransportFailureAfterStatusCommit { get; set; }

        /// <summary>
        /// When set, another join's claim with the same claim stamp but its own write token commits just before the join's claim write,
        /// which then fails with 412 (clears itself), as an overlapping process's claim in the same millisecond would.
        /// </summary>
        public bool ForeignClaimWithTheSameStamp { get; set; }

        /// <summary>The write token of the claim <see cref="ForeignClaimWithTheSameStamp"/> commits.</summary>
        public const string ForeignJoinWriteToken = "foreign-join";

        /// <summary>
        /// When set, the first claim write fails without committing yet; it commits when the second claim write arrives, which then fails
        /// with 412 (clears itself then).
        /// </summary>
        public bool FirstClaimCommitsLate { get; set; }

        private Action<JobStatusEntity>? _pendingClaim;

        public List<JobStatusEntity> StatusWrites { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(IJobService.GetJobStatusAsync) && FailReadsAfterStatusWrite && StatusWrites.Count > 0)
            {
                return Task.FromException<JobStatusEntity?>(new IOException("Fixture: job table unavailable"));
            }

            JobStatusEntity? statusWrite = null;
            var claimed = false;
            if (method.Name == nameof(IJobService.MergeWithETagAsync))
            {
                var marker = new JobStatusEntity();
                ((Action<JobStatusEntity>)args![1]!)(marker);
                statusWrite = marker.Status != null ? marker : null;
                claimed = marker.JoinClaimedAt != null;
                if (claimed && ForeignClaimWithTheSameStamp)
                {
                    ForeignClaimWithTheSameStamp = false;
                    Target.MergeJobFieldsAsync((string)args[0]!, e =>
                    {
                        e.JoinClaimedAt = marker.JoinClaimedAt;
                        e.EpisodeId = marker.EpisodeId;
                        e.EpisodeUploadedAt = marker.EpisodeUploadedAt;
                        e.PublishedTitle = marker.PublishedTitle;
                        e.JoinWriteToken = ForeignJoinWriteToken;
                    }).GetAwaiter().GetResult();

                    return Task.FromException(new Azure.RequestFailedException(412, "Precondition failed"));
                }
            }
            else if (method.Name == nameof(IJobService.TryReplaceAsync) && args![0] is JobStatusEntity { Status: not null } replacement)
            {
                statusWrite = Snapshot(replacement);
            }

            if (claimed && FirstClaimCommitsLate)
            {
                if (_pendingClaim == null)
                {
                    _pendingClaim = (Action<JobStatusEntity>)args![1]!;

                    return Task.FromException(new IOException("Fixture: connection lost while the write is pending"));
                }

                FirstClaimCommitsLate = false;
                Target.MergeJobFieldsAsync((string)args![0]!, _pendingClaim).GetAwaiter().GetResult();

                return Task.FromException(new Azure.RequestFailedException(412, "Precondition failed"));
            }

            if (statusWrite != null)
            {
                if (BeforeStatusWrite is { } callback)
                {
                    BeforeStatusWrite = null;
                    callback();
                }

                StatusWrites.Add(statusWrite);
            }

            object? result;
            try
            {
                result = method.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                // A synchronous throw of the target (its simulated 412) reaches the caller as itself, as it would without the proxy
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();

                throw;
            }

            if (claimed && AfterClaimWrite is { } afterClaim)
            {
                AfterClaimWrite = null;
                afterClaim();
            }

            if (statusWrite != null && ConflictAfterStatusCommit)
            {
                ConflictAfterStatusCommit = false;

                return method.Name == nameof(IJobService.TryReplaceAsync)
                    ? Task.FromResult(false)
                    : Task.FromException(new Azure.RequestFailedException(412, "Precondition failed"));
            }

            if (statusWrite != null && TransportFailureAfterStatusCommit)
            {
                TransportFailureAfterStatusCommit = false;
                var lost = new AggregateException(new IOException("Fixture: connection reset after the write committed"));

                return method.Name == nameof(IJobService.TryReplaceAsync) ? Task.FromException<bool>(lost) : Task.FromException(lost);
            }

            if (claimed && ConflictAfterClaimCommit)
            {
                ConflictAfterClaimCommit = false;

                return Task.FromException(new Azure.RequestFailedException(412, "Precondition failed"));
            }

            if (claimed && CancelAfterClaimCommit is { } source)
            {
                CancelAfterClaimCommit = null;
                source.Cancel();
                ((CancellationToken)args![3]!).ThrowIfCancellationRequested();
            }

            return result;
        }
    }
}
