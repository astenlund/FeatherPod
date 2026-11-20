using FeatherPod.Functions;
using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Tests for transcript and normalization retry (<see cref="JobRetryService"/>), the joins and guards that keep work of an earlier
/// attempt from changing a retried job, and the recurring recovery. Uses the in-memory job service and file-based blob storage.
/// </summary>
[Collection("Sequential")]
public sealed class JobRetryServiceTests : IDisposable
{
    private const string FeedId = "retry-feed";
    private const string EpisodeId = "ep-retry";
    private const string FileName = "show.mp3";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "retry-test-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryJobService _jobs = new();
    private readonly TestBlobStorageService _blob;
    private readonly EpisodeService _episodes;
    private readonly TranscriptionChannel _channel = new();
    private readonly TranscriptionTestHarness.FakeSpeechTranscriptionService _speech = new();
    private readonly JobCompletionService _completion;
    private readonly JobRetryService _retry;

    public JobRetryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _blob = new TestBlobStorageService(_root);
        _episodes = new EpisodeService(_blob, NullLogger<EpisodeService>.Instance);
        var push = new StubPushNotificationService(new StubBlobService(), _episodes, _jobs);
        _completion = new JobCompletionService(_jobs, _episodes, _blob, new NullJobProgressChannel(), new NullFeedEventChannel(), push, NullLogger<JobCompletionService>.Instance);
        _retry = new JobRetryService(_jobs, _episodes, _blob, _channel, _speech, new NullJobProgressChannel(), _completion, push, new ConfigurationBuilder().Build(), NullLogger<JobRetryService>.Instance);
    }

    public void Dispose()
    {
        _episodes.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a test; the temp directory is cleaned up eventually
        }
    }

    // ===== Transcript retry: eligibility =====

    [Fact]
    public async Task RetryTranscript_MissingEpisode_RefusedNotFound()
    {
        // Arrange
        await CreateFeedAsync();

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, "no-such-episode", CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TranscriptStatus.Available)]
    public async Task RetryTranscript_TranscriptNotFailed_RefusedNotRetryable(TranscriptStatus? status)
    {
        // Arrange
        await CreateEpisodeAsync(status);

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.NotRetryable, result.Outcome);
        Assert.Empty(await _jobs.GetActiveJobsAsync());
    }

    [Fact]
    public async Task RetryTranscript_SpeechUnavailable_RefusedSpeechUnavailable()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        _speech.IsAvailable = false;

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.SpeechUnavailable, result.Outcome);
    }

    [Fact]
    public async Task RetryTranscript_AudioMissing_RefusedAudioMissing()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed, writeAudio: false);

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.AudioMissing, result.Outcome);
    }

    [Fact]
    public async Task RetryTranscript_RetryAlreadyActive_RefusedRetryActive()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var first = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Act
        var second = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.Accepted, first.Outcome);
        Assert.Equal(JobRetryOutcome.RetryActive, second.Outcome);
    }

    [Fact]
    public async Task RetryTranscript_ConcurrentRequests_CreateOneRetryJob()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None))));

        // Assert
        Assert.Single(results, r => r.Outcome == JobRetryOutcome.Accepted);
        Assert.All(results.Where(r => r.Outcome != JobRetryOutcome.Accepted), r => Assert.Equal(JobRetryOutcome.RetryActive, r.Outcome));
        Assert.Single(await _jobs.GetActiveJobsAsync(), j => JobKinds.IsTranscriptRetry(j.Kind));
    }

    [Fact]
    public async Task RetryTranscript_Accepted_CreatesTranscriptionOnlyJobFromEpisodeAudio()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.Accepted, result.Outcome);
        var job = _jobs.GetEntity(result.Job!.JobId)!;
        Assert.Equal(JobKinds.TranscriptRetry, job.Kind);
        Assert.Equal(EpisodeId, job.EpisodeId);
        Assert.Equal(true, job.NormalizationComplete);
        Assert.Equal(TranscriptionStatuses.Queued, job.TranscriptionStatus);
        var request = Assert.Single(await DrainChannelAsync());
        Assert.True(request.FromEpisodeAudio);
        Assert.Equal(result.Job.JobId, request.JobId);
    }

    // ===== Transcript retry: join =====

    [Fact]
    public async Task TranscriptRetryJoin_Success_PublishesTranscriptAndBumpsFeedVersion()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        var versionBefore = (await _episodes.GetFeedSnapshotAsync(FeedId))!.Value.LastModified;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        await Task.Delay(20);

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        var job = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        var episode = (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!;
        Assert.Equal(TranscriptStatus.Available, episode.TranscriptStatus);
        Assert.Single(await _episodes.GetAllEpisodesAsync(FeedId));
        await using var transcript = await _blob.DownloadTranscriptAsync(FeedId, EpisodeId);
        Assert.Equal("WEBVTT\n\nretried", await new StreamReader(transcript!).ReadToEndAsync());
        Assert.True((await _episodes.GetFeedSnapshotAsync(FeedId))!.Value.LastModified > versionBefore);
        Assert.Null(await _blob.DownloadPendingTranscriptAsync(FeedId, jobId, 1));
    }

    [Fact]
    public async Task TranscriptRetryJoin_TranscriptionFailed_JobCompletedEpisodeStillFailed()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Failed);

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity(jobId)!.Status);
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        Assert.Null(await _blob.DownloadTranscriptAsync(FeedId, EpisodeId));
    }

    [Fact]
    public async Task TranscriptRetryJoin_EpisodeDeletedMidRetry_TerminalJobAndNothingLeftBehind()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        await _episodes.DeleteEpisodeAsync(FeedId, EpisodeId);

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity(jobId)!.Status);
        Assert.Null(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
        Assert.Null(await _blob.DownloadTranscriptAsync(FeedId, EpisodeId));
    }

    [Fact]
    public async Task TranscriptRetryJoin_EpisodeRecreatedByReupload_LeavesReuploadTranscriptIntact()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nstale retry");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        await _episodes.DeleteEpisodeAsync(FeedId, EpisodeId);
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Available));
        await _blob.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nreupload");

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        await using var transcript = await _blob.DownloadTranscriptAsync(FeedId, EpisodeId);
        Assert.Equal("WEBVTT\n\nreupload", await new StreamReader(transcript!).ReadToEndAsync());
    }

    [Fact]
    public async Task TranscriptRetry_SucceedsAfterOriginalJobRecordIsGone()
    {
        // Arrange: the episode's original upload job no longer exists anywhere (purged after JobRetentionDays)
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        Assert.Empty(await _jobs.GetActiveJobsAsync());

        // Act
        var result = await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.Accepted, result.Outcome);
    }

    // ===== Normalization retry =====

    [Fact]
    public async Task RetryNormalization_JobNotFailedInNormalization_RefusedNotRetryable()
    {
        // Arrange
        var completed = SeedFailedNormalizationJob("job-completed");
        completed.Status = nameof(JobStatus.Completed);
        var cancelled = SeedFailedNormalizationJob("job-cancelled");
        cancelled.Status = nameof(JobStatus.Cancelled);

        // Act
        var completedResult = await _retry.RetryNormalizationAsync(completed, CancellationToken.None);
        var cancelledResult = await _retry.RetryNormalizationAsync(cancelled, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.NotRetryable, completedResult.Outcome);
        Assert.Equal(JobRetryOutcome.NotRetryable, cancelledResult.Outcome);
    }

    [Fact]
    public async Task RetryNormalization_SourceGone_RefusedSourceGone()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-gone", writePending: false);

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.SourceGone, result.Outcome);
        Assert.Equal(nameof(JobStatus.Failed), _jobs.GetEntity("job-gone")!.Status);
    }

    [Fact]
    public async Task RetryNormalization_LostReplaceRace_RefusedConflictWithoutRequeue()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-race");
        _jobs.RefuseNextReplace = true;

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.Conflict, result.Outcome);
        Assert.Empty(_jobs.QueuedMessages);
    }

    [Fact]
    public async Task RetryNormalization_DecidedOnAnEarlierAttempt_RefusedWithoutRequeue()
    {
        // Arrange: the caller saw attempt 1 fail, but the job has since been retried and its attempt 2 failed too
        var job = SeedFailedNormalizationJob("job-stale-view");
        job.Attempt = 2;

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None, expectedAttempt: 1);

        // Assert
        Assert.Equal(JobRetryOutcome.Conflict, result.Outcome);
        Assert.Empty(_jobs.QueuedMessages);
        Assert.Equal(2, _jobs.GetEntity("job-stale-view")!.Attempt);
    }

    [Theory]
    [InlineData(TranscriptionStatuses.Failed)]
    [InlineData(null)]
    public async Task RetryNormalization_SpeechConfigured_JoinWaitsForTheNewAttemptsTranscription(string? transcriptionStatus)
    {
        // Arrange
        await CreateFeedAsync();
        var job = SeedFailedNormalizationJob("job-join-waits", transcriptionStatus: transcriptionStatus);
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Act: attempt 2's normalization completes before its transcription
        await _completion.HandleNormalizationCompleteAsync("job-join-waits", new NormalizationCompleteRequest { Success = true, NormalizedFileSize = 3, Attempt = 2 }, CancellationToken.None);
        var afterNormalization = _jobs.GetEntity("job-join-waits")!.GetJobStatus();
        await _blob.UploadPendingTranscriptAsync(FeedId, "job-join-waits", 2, "WEBVTT\n\nattempt two");
        await _jobs.MergeJobFieldsAsync("job-join-waits", e => e.TranscriptionStatus = TranscriptionStatuses.Completed, CancellationToken.None, expectedAttempt: 2);
        await _completion.TryCompleteJobAsync("job-join-waits", CancellationToken.None);

        // Assert
        Assert.False(afterNormalization.IsTerminal());
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity("job-join-waits")!.Status);
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(TranscriptionStatuses.Failed, TranscriptStatus.Failed)]
    [InlineData(TranscriptionStatuses.Queued, TranscriptStatus.Failed)]
    [InlineData(TranscriptionStatuses.Running, TranscriptStatus.Failed)]
    public async Task RetryNormalization_SpeechUnconfigured_PublishesTheEpisodeWithTheSpecifiedTranscriptStatus(string? transcriptionStatus, TranscriptStatus? published)
    {
        // Arrange
        await CreateFeedAsync();
        _speech.IsAvailable = false;
        var job = SeedFailedNormalizationJob("job-nospeech-join", transcriptionStatus: transcriptionStatus);
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Act
        await _completion.HandleNormalizationCompleteAsync("job-nospeech-join", new NormalizationCompleteRequest { Success = true, NormalizedFileSize = 3, Attempt = 2 }, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity("job-nospeech-join")!.Status);
        Assert.Equal(published, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
    }

    [Fact]
    public async Task RetryNormalization_Accepted_ResetsToNewAttemptAndRequeues()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-reset", transcriptionStatus: TranscriptionStatuses.Completed);
        var oldQueuedAt = job.QueuedAt;

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.Accepted, result.Outcome);
        var reset = _jobs.GetEntity("job-reset")!;
        Assert.Equal(2, reset.Attempt);
        Assert.Equal(nameof(JobStatus.Queued), reset.Status);
        Assert.True(reset.QueuedAt > oldQueuedAt);
        Assert.Null(reset.CompletedAt);
        Assert.Null(reset.NormalizationComplete);
        Assert.Null(reset.NormalizationError);
        Assert.Equal(TranscriptionStatuses.Completed, reset.TranscriptionStatus);
        var message = Assert.Single(_jobs.QueuedMessages);
        Assert.Equal(2, message.Attempt);
        Assert.Empty(await DrainChannelAsync());
        Assert.Equal(2, result.Job!.Attempt);
    }

    [Fact]
    public async Task RetryNormalization_ThenJoin_CreatesEpisodeKeepingCompletedTranscription()
    {
        // Arrange
        await CreateFeedAsync();
        var job = SeedFailedNormalizationJob("job-join", transcriptionStatus: TranscriptionStatuses.Completed);
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Act
        await _completion.HandleNormalizationCompleteAsync("job-join", new NormalizationCompleteRequest { Success = true, NormalizedFileSize = 100, AudioDurationMs = 1000, Attempt = 2 }, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity("job-join")!.Status);
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
    }

    [Theory]
    [InlineData(TranscriptionStatuses.Failed)]
    [InlineData(null)]
    [InlineData(TranscriptionStatuses.Queued)]
    [InlineData(TranscriptionStatuses.Running)]
    public async Task RetryNormalization_SpeechConfigured_RerunsUnfinishedTranscriptionForNewAttempt(string? transcriptionStatus)
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-rerun", transcriptionStatus: transcriptionStatus);

        // Act
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptionStatuses.Queued, _jobs.GetEntity("job-rerun")!.TranscriptionStatus);
        var request = Assert.Single(await DrainChannelAsync());
        Assert.Equal(2, request.Attempt);
        Assert.False(request.FromEpisodeAudio);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(TranscriptionStatuses.Failed, TranscriptionStatuses.Failed)]
    [InlineData(TranscriptionStatuses.Queued, TranscriptionStatuses.Failed)]
    [InlineData(TranscriptionStatuses.Running, TranscriptionStatuses.Failed)]
    [InlineData(TranscriptionStatuses.Completed, TranscriptionStatuses.Completed)]
    public void ResetForNormalizationRetry_SpeechUnconfigured_NeverFailsAnUnattemptedTranscription(string? before, string? after)
    {
        // Arrange
        var job = new JobStatusEntity { RowKey = "job-nospeech", Status = nameof(JobStatus.Failed), NormalizationError = "boom", TranscriptionStatus = before };

        // Act
        var rerun = JobRetryService.ResetForNormalizationRetry(job, speechAvailable: false, DateTimeOffset.UtcNow);

        // Assert
        Assert.False(rerun);
        Assert.Equal(after, job.TranscriptionStatus);
    }

    [Fact]
    public async Task RetryNormalization_RequeueFails_JobReturnedToFailedAndRetryableAgain()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-qfail");
        _jobs.QueueThrows = new InvalidOperationException("queue down");

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.QueueFailed, result.Outcome);
        var failed = _jobs.GetEntity("job-qfail")!;
        Assert.Equal(nameof(JobStatus.Failed), failed.Status);
        Assert.Equal(2, failed.Attempt);
        Assert.False(string.IsNullOrEmpty(failed.NormalizationError));
        _jobs.QueueThrows = null;
        var again = await _retry.RetryNormalizationAsync(failed, CancellationToken.None);
        Assert.Equal(JobRetryOutcome.Accepted, again.Outcome);
        Assert.Equal(3, _jobs.GetEntity("job-qfail")!.Attempt);
    }

    [Fact]
    public async Task NormalizationCompleteOfEarlierAttempt_ChangesNothingOnNewAttempt()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-late");
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Act
        await _completion.HandleNormalizationCompleteAsync("job-late", new NormalizationCompleteRequest { Success = false, Error = "late failure of attempt 1", Attempt = 1 }, CancellationToken.None);
        await _completion.HandleNormalizationCompleteAsync("job-late", new NormalizationCompleteRequest { Success = false, Error = "late failure without attempt" }, CancellationToken.None);

        // Assert
        var entity = _jobs.GetEntity("job-late")!;
        Assert.Equal(nameof(JobStatus.Queued), entity.Status);
        Assert.Null(entity.NormalizationComplete);
        Assert.Null(entity.NormalizationError);
    }

    [Fact]
    public async Task TranscriptionWriteOfEarlierAttempt_ChangesNothingOnNewAttempt()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-stale-run", transcriptionStatus: TranscriptionStatuses.Running);
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Act: the still-live run of attempt 1 records its result
        var merged = await _jobs.MergeJobFieldsAsync("job-stale-run", e => e.TranscriptionStatus = TranscriptionStatuses.Failed, CancellationToken.None, expectedAttempt: 1);

        // Assert
        Assert.Null(merged);
        Assert.Equal(TranscriptionStatuses.Queued, _jobs.GetEntity("job-stale-run")!.TranscriptionStatus);
    }

    [Theory]
    [InlineData(nameof(JobStatus.Failed), 1, 1, "job is already Failed")]
    [InlineData(nameof(JobStatus.Completed), 2, 2, "job is already Completed")]
    [InlineData(nameof(JobStatus.Queued), 2, 1, "job is on attempt 2")]
    [InlineData(nameof(JobStatus.Queued), 2, null, "job is on attempt 2")]
    public void FunctionDelivery_TerminalJobOrEarlierAttempt_Skipped(string status, int jobAttempt, int? messageAttempt, string reason)
    {
        // Arrange
        var entity = new JobStatusEntity { RowKey = "job-d", Status = status, Attempt = jobAttempt };
        var message = NewMessage(messageAttempt);

        // Act
        var skip = NormalizationFunction.SkipDeliveryReason(entity, message);

        // Assert
        Assert.Equal(reason, skip);
    }

    [Fact]
    public void FunctionDelivery_CurrentAttemptWithItsNormalizationResultRecorded_Skipped()
    {
        // Arrange: a repeat delivery after the result was signalled, while the job's join runs (claimed, still Processing)
        var entity = new JobStatusEntity { RowKey = "job-d", Status = nameof(JobStatus.Processing), Attempt = 1, NormalizationComplete = true, JoinClaimedAt = DateTimeOffset.UtcNow };

        // Act
        var skip = NormalizationFunction.SkipDeliveryReason(entity, NewMessage(1));

        // Assert
        Assert.Equal("its normalization result is already recorded", skip);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(2, 2)]
    public void FunctionDelivery_CurrentAttempt_Runs(int? jobAttempt, int? messageAttempt)
    {
        // Arrange
        var entity = new JobStatusEntity { RowKey = "job-d", Status = nameof(JobStatus.Queued), Attempt = jobAttempt };

        // Act
        var skip = NormalizationFunction.SkipDeliveryReason(entity, NewMessage(messageAttempt));

        // Assert
        Assert.Null(skip);
    }

    // ===== Recurring recovery =====

    [Fact]
    public async Task Recovery_UnpickedResetPastBound_ReturnedToFailedAndRetryable()
    {
        // Arrange
        var job = SeedFailedNormalizationJob("job-unpicked");
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);
        var later = DateTimeOffset.UtcNow.AddHours(7);

        // Act
        await _retry.RecoverStalledRetriesAsync(later, CancellationToken.None);

        // Assert
        var failed = _jobs.GetEntity("job-unpicked")!;
        Assert.Equal(nameof(JobStatus.Failed), failed.Status);
        Assert.Equal(JobRetryService.RetryNotStartedError, failed.NormalizationError);
        Assert.Equal(JobRetryOutcome.Accepted, (await _retry.RetryNormalizationAsync(failed, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Recovery_ResetWithinBoundOrPickedUp_LeftAlone()
    {
        // Arrange
        var waiting = SeedFailedNormalizationJob("job-waiting");
        await _retry.RetryNormalizationAsync(waiting, CancellationToken.None);
        var picked = SeedFailedNormalizationJob("job-picked");
        await _retry.RetryNormalizationAsync(picked, CancellationToken.None);
        await _jobs.MergeJobFieldsAsync("job-picked", e => e.Status = nameof(JobStatus.Processing));

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        var waitingWithinBound = _jobs.GetEntity("job-waiting")!.Status;
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(7), CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Queued), waitingWithinBound);
        Assert.Equal(nameof(JobStatus.Processing), _jobs.GetEntity("job-picked")!.Status);
    }

    [Fact]
    public void Recovery_UnpickedPredicate_RespectsBound()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var job = new JobStatusEntity { RowKey = "j", Status = nameof(JobStatus.Queued), Attempt = 2, NormalizationStage = nameof(NormalizationStage.Queued), QueuedAt = now.AddMinutes(-30) };

        // Act
        var withinBound = JobRetryService.IsUnpickedNormalizationRetry(job, now, TimeSpan.FromHours(1));
        var pastBound = JobRetryService.IsUnpickedNormalizationRetry(job, now.AddHours(2), TimeSpan.FromHours(1));
        job.Attempt = 1;
        var firstAttempt = JobRetryService.IsUnpickedNormalizationRetry(job, now.AddHours(2), TimeSpan.FromHours(1));

        // Assert
        Assert.False(withinBound);
        Assert.True(pastBound);
        Assert.False(firstAttempt);
    }

    [Fact]
    public async Task Recovery_StalledTranscriptRetry_EndedAndEpisodeRetryableAgain()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Running);

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);

        // Assert
        var ended = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), ended.Status);
        Assert.Equal(TranscriptionStatuses.Failed, ended.TranscriptionStatus);
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        Assert.Equal(JobRetryOutcome.Accepted, (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Recovery_JoinStoppedAfterItsClaim_EndsJobWithFailedTranscript()
    {
        // Arrange: a retry whose join claimed the job and then stopped before writing its terminal status
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _jobs.MergeJobFieldsAsync(jobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Completed;
            e.JoinClaimedAt = DateTimeOffset.UtcNow;
        });

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);

        // Assert
        var job = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Equal(JobCompletionService.TranscriptNotPublishedError, job.TranscriptionError);
    }

    [Fact]
    public async Task Recovery_TranscriptRetryWithinBound_LeftAlone()
    {
        // Arrange
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Queued), _jobs.GetEntity(jobId)!.Status);
    }

    // ===== Repair reproductions =====

    [Fact]
    public async Task Join_ConflictWithResetToNewAttempt_LeavesNewAttemptRunning()
    {
        // Arrange: attempt 1 has both tracks terminal with normalization failed; the join's write conflicts with a reset to attempt 2
        var job = SeedFailedNormalizationJob("job-join-race");
        job.Status = nameof(JobStatus.Processing);
        job.CompletedAt = null;
        _jobs.BeforeNextETagMergeConflict = () =>
        {
            var reset = _jobs.GetEntity("job-join-race")!;
            reset.Status = nameof(JobStatus.Failed);
            JobRetryService.ResetForNormalizationRetry(reset, speechAvailable: false, DateTimeOffset.UtcNow);
        };

        // Act
        await _completion.TryCompleteJobAsync("job-join-race", CancellationToken.None);

        // Assert
        var entity = _jobs.GetEntity("job-join-race")!;
        Assert.Equal(2, entity.Attempt);
        Assert.Equal(nameof(JobStatus.Queued), entity.Status);
    }

    [Fact]
    public async Task TranscriptRetryJoin_EpisodeRecreatedFailedByCompletedReupload_DoesNotOverwriteItsTranscript()
    {
        // Arrange: the retry runs; meanwhile the episode is deleted and a same-file re-upload completes, re-creating it with a failed
        // transcript of its own and writing the transcript location
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nstale retry");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        await _episodes.DeleteEpisodeAsync(FeedId, EpisodeId);
        _jobs.SetEntity(new JobStatusEntity { RowKey = "reupload", FeedId = FeedId, EpisodeId = EpisodeId, Status = nameof(JobStatus.Completed), QueuedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow });
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Failed) with { UploadedAt = DateTime.UtcNow.AddMinutes(1) });
        await _blob.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nreupload");

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        await using var transcript = await _blob.DownloadTranscriptAsync(FeedId, EpisodeId);
        Assert.Equal("WEBVTT\n\nreupload", await new StreamReader(transcript!).ReadToEndAsync());
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
    }

    [Fact]
    public async Task TranscriptRetryJoin_PublishFails_ReportsFailedTranscript()
    {
        // Arrange
        var failingBlob = new FailingTranscriptBlobService(_root);
        var episodes = new EpisodeService(failingBlob, NullLogger<EpisodeService>.Instance);
        var push = new StubPushNotificationService(new StubBlobService(), episodes, _jobs);
        var completion = new JobCompletionService(_jobs, episodes, failingBlob, new NullJobProgressChannel(), new NullFeedEventChannel(), push, NullLogger<JobCompletionService>.Instance);
        var retry = new JobRetryService(_jobs, episodes, failingBlob, _channel, _speech, new NullJobProgressChannel(), completion, push, new ConfigurationBuilder().Build(), NullLogger<JobRetryService>.Instance);
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Retry Podcast", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        await episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Failed));
        Directory.CreateDirectory(Path.Combine(_root, FeedId, "audio"));
        await File.WriteAllBytesAsync(Path.Combine(_root, FeedId, "audio", FileName), [1, 2, 3]);
        var jobId = (await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await failingBlob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);

        // Act
        await completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        var job = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Equal(TranscriptStatus.Failed, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        episodes.Dispose();
    }

    [Fact]
    public async Task EndedRetryRunFinishingLate_LeavesNewerRetryTranscriptIntact()
    {
        // Arrange: retry R1 stalls and is ended; retry R2 then publishes
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var r1 = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _jobs.MergeJobFieldsAsync(r1, e => e.TranscriptionStatus = TranscriptionStatuses.Running);
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);
        var r2 = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, r2, 1, "WEBVTT\n\nnewer");
        await _jobs.MergeJobFieldsAsync(r2, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        await _completion.TryCompleteJobAsync(r2, CancellationToken.None);

        // Act: R1's run finishes late, staging its transcript and attempting its status write and join
        await _blob.UploadPendingTranscriptAsync(FeedId, r1, 1, "WEBVTT\n\nlate");
        var merged = await _jobs.MergeJobFieldsAsync(r1, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, CancellationToken.None, expectedAttempt: 1);
        await _completion.TryCompleteJobAsync(r1, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
        await using var transcript = await _blob.DownloadTranscriptAsync(FeedId, EpisodeId);
        Assert.Equal("WEBVTT\n\nnewer", await new StreamReader(transcript!).ReadToEndAsync());
        Assert.Equal(TranscriptionStatuses.Failed, _jobs.GetEntity(r1)!.TranscriptionStatus);
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity(r1)!.Status);
        Assert.Null(merged);
    }

    [Fact]
    public async Task RetryNormalization_CompletedTranscriptionWithoutStagedTranscript_IsRunAgain()
    {
        // Arrange: a completed transcription whose staged transcript is missing (as for a job transcribed before staging existed)
        var job = SeedFailedNormalizationJob("job-unstaged", transcriptionStatus: TranscriptionStatuses.Completed);
        File.Delete(Path.Combine(_root, FeedId, "pending", "job-unstaged", "_transcript-a1.vtt"));

        // Act
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptionStatuses.Queued, _jobs.GetEntity("job-unstaged")!.TranscriptionStatus);
        Assert.Single(await DrainChannelAsync());
    }

    [Fact]
    public async Task RetryNormalization_SourceClaimedByCleanup_RefusedSourceGone()
    {
        // Arrange: the scheduled cleanup has claimed the source for deletion, though the blob is still there
        var job = SeedFailedNormalizationJob("job-swept");
        job.SourceSweptAt = DateTimeOffset.UtcNow;

        // Act
        var result = await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(JobRetryOutcome.SourceGone, result.Outcome);
        Assert.Empty(_jobs.QueuedMessages);
    }

    [Fact]
    public async Task TranscriptRetryJoin_WhilePublishing_JobIsNotYetTerminal()
    {
        // Arrange: a blob store that records the job's status at the moment the transcript is written
        string? statusDuringPublish = null;
        string? jobIdHolder = null;
        var hooked = new HookedBlobService(_root) { OnUploadTranscript = () => statusDuringPublish = _jobs.GetEntity(jobIdHolder!)!.Status };
        var (episodes, completion, retry) = BuildServices(hooked);
        await SeedEpisodeAsync(episodes, TranscriptStatus.Failed);
        jobIdHolder = (await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await hooked.UploadPendingTranscriptAsync(FeedId, jobIdHolder, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobIdHolder, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);

        // Act
        await completion.TryCompleteJobAsync(jobIdHolder, CancellationToken.None);

        // Assert
        Assert.NotNull(statusDuringPublish);
        Assert.False(Enum.Parse<JobStatus>(statusDuringPublish!).IsTerminal(), $"the job was already {statusDuringPublish} while its transcript was being published");
        Assert.Equal(TranscriptionStatuses.Completed, _jobs.GetEntity(jobIdHolder)!.TranscriptionStatus);
        episodes.Dispose();
    }

    [Fact]
    public async Task UploadJoin_LegacyJobWithCanonicalTranscript_KeepsItsCompletedTranscription()
    {
        // Arrange: a job transcribed before staging existed (no attempt), whose run wrote the episode's transcript location directly
        await CreateFeedAsync();
        _jobs.SetEntity(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = "job-legacy",
            Status = nameof(JobStatus.Processing),
            FeedId = FeedId,
            FileName = FileName,
            EpisodeId = EpisodeId,
            Title = "Legacy",
            Source = nameof(UploadSource.Browser),
            OriginalFileSize = 3,
            QueuedAt = DateTimeOffset.UtcNow,
            NormalizationComplete = true,
            NormalizedFileSize = 3,
            TranscriptionStatus = TranscriptionStatuses.Completed,
        });
        await _blob.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nlegacy");

        // Act
        await _completion.TryCompleteJobAsync("job-legacy", CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.TranscriptStatus);
    }

    [Fact]
    public async Task RetryNormalization_LegacyCompletedTranscription_IsKeptNotRunAgain()
    {
        // Arrange: a legacy job (no attempt) whose completed transcript sits at the episode's location, not staged
        var job = SeedFailedNormalizationJob("job-legacy-keep", transcriptionStatus: TranscriptionStatuses.Completed);
        File.Delete(Path.Combine(_root, FeedId, "pending", "job-legacy-keep", "_transcript-a1.vtt"));
        await _blob.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nlegacy");

        // Act
        await _retry.RetryNormalizationAsync(job, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptionStatuses.Completed, _jobs.GetEntity("job-legacy-keep")!.TranscriptionStatus);
        Assert.Empty(await DrainChannelAsync());
        Assert.Equal("WEBVTT\n\nlegacy", await _blob.DownloadPendingTranscriptAsync(FeedId, "job-legacy-keep", 2));
    }

    [Fact]
    public async Task MoveEpisode_WhileItsRetryPublishes_MovesThePublishedEpisodeOnce()
    {
        // Arrange: the retry publishes while the move is copying the audio
        var hooked = new HookedBlobService(_root);
        var (episodes, _, _) = BuildServices(hooked);
        var uploadedAt = await SeedEpisodeAsync(episodes, TranscriptStatus.Failed);
        await episodes.CreateFeedAsync(new FeedConfig { Id = "retry-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        hooked.OnDownloadAudioToTemp = () => episodes.PublishTranscriptIfAsync(FeedId, EpisodeId, uploadedAt, () => hooked.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\npublished")).GetAwaiter().GetResult();

        // Act
        var moved = await episodes.MoveEpisodeAsync(EpisodeId, FeedId, "retry-target");

        // Assert
        Assert.Empty(await episodes.GetAllEpisodesAsync(FeedId));
        var target = Assert.Single(await episodes.GetAllEpisodesAsync("retry-target"));
        Assert.Equal(TranscriptStatus.Available, target.TranscriptStatus);
        await using var transcript = await hooked.DownloadTranscriptAsync("retry-target", moved.Id);
        Assert.Equal("WEBVTT\n\npublished", await new StreamReader(transcript!).ReadToEndAsync());
        episodes.Dispose();
    }

    [Fact]
    public async Task RecoveryDuringALiveJoinsPublish_JobAndEpisodeOutcomesAgree()
    {
        // Arrange: while the join is writing the transcript, the recurring recovery runs past the stall bound
        JobRetryService? retryRef = null;
        var hooked = new HookedBlobService(_root);
        var (episodes, completion, retry) = BuildServices(hooked);
        retryRef = retry;
        await SeedEpisodeAsync(episodes, TranscriptStatus.Failed);
        var jobId = (await retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await hooked.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        hooked.OnUploadTranscript = () =>
        {
            hooked.OnUploadTranscript = null;
            retryRef.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None).GetAwaiter().GetResult();
        };

        // Act
        await completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert: the job reports a published transcript exactly when the episode has one
        var job = _jobs.GetEntity(jobId)!;
        var episode = (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(episode.TranscriptStatus == TranscriptStatus.Available, job.TranscriptionStatus == TranscriptionStatuses.Completed);
        episodes.Dispose();
    }

    [Fact]
    public async Task MoveEpisode_TargetHasEpisodeWithTheFileName_IsRefusedAndKeepsTheTargetEpisodesFiles()
    {
        // Arrange: the target feed already has a published episode at the same file and id the move would create
        var hooked = new HookedBlobService(_root);
        var (episodes, _, _) = BuildServices(hooked);
        await SeedEpisodeAsync(episodes, TranscriptStatus.Failed);
        await episodes.CreateFeedAsync(new FeedConfig { Id = "retry-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        var targetId = Episode.GenerateId("retry-target", FileName, 3);
        await episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Available) with { Id = targetId, FeedId = "retry-target" }, CancellationToken.None, "WEBVTT\n\ntarget");
        Directory.CreateDirectory(Path.Combine(_root, "retry-target", "audio"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "retry-target", "audio", FileName), [9, 9, 9]);

        // Act
        var exception = await Record.ExceptionAsync(() => episodes.MoveEpisodeAsync(EpisodeId, FeedId, "retry-target"));

        // Assert: refused as a conflict, both feeds unchanged, the target episode's audio and transcript untouched
        Assert.Equal(EpisodeConflictReason.TargetHasEpisode, Assert.IsType<EpisodeConflictException>(exception).Reason);
        Assert.NotNull(await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
        Assert.Single(await episodes.GetAllEpisodesAsync("retry-target"));
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(Path.Combine(_root, "retry-target", "audio", FileName)));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(_root, FeedId, "audio", FileName)));
        await using var transcript = await hooked.DownloadTranscriptAsync("retry-target", targetId);
        Assert.Equal("WEBVTT\n\ntarget", await new StreamReader(transcript!).ReadToEndAsync());
        episodes.Dispose();
    }

    [Fact]
    public async Task MoveEpisode_SourceDeletedMidMove_UploadPublishedAfterItsCheck_KeepsTheUploadsAudio()
    {
        // Arrange: the source is deleted during the move, and an upload publishes the same file to the empty target feed after the
        // move's commit section has looked and before any later step of the move runs
        var hooked = new HookedBlobService(_root);
        var (episodes, _, _) = BuildServices(hooked);
        await SeedEpisodeAsync(episodes, TranscriptStatus.Available);
        await hooked.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nsource");
        await episodes.CreateFeedAsync(new FeedConfig { Id = "retry-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        var targetId = Episode.GenerateId("retry-target", FileName, 3);
        hooked.OnDownloadAudioToTemp = () => episodes.DeleteEpisodeAsync(FeedId, EpisodeId).GetAwaiter().GetResult();
        hooked.BeforeTargetCleanup = ("retry-target", () =>
        {
            File.WriteAllBytes(Path.Combine(_root, "retry-target", "audio", FileName), [7, 7, 7]);
            episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Available) with { Id = targetId, FeedId = "retry-target" }, CancellationToken.None, "WEBVTT\n\nupload").GetAwaiter().GetResult();
        });

        // Act
        var exception = await Record.ExceptionAsync(() => episodes.MoveEpisodeAsync(EpisodeId, FeedId, "retry-target"));
        hooked.RunPendingTargetCleanupHook();

        // Assert: the published upload keeps its audio and transcript
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Single(await episodes.GetAllEpisodesAsync("retry-target"));
        Assert.True(File.Exists(Path.Combine(_root, "retry-target", "audio", FileName)));
        await using var transcript = await hooked.DownloadTranscriptAsync("retry-target", targetId);
        Assert.NotNull(transcript);
        episodes.Dispose();
    }

    private (EpisodeService Episodes, JobCompletionService Completion, JobRetryService Retry) BuildServices(TestBlobStorageService blob)
    {
        var episodes = new EpisodeService(blob, NullLogger<EpisodeService>.Instance);
        var push = new StubPushNotificationService(new StubBlobService(), episodes, _jobs);
        var completion = new JobCompletionService(_jobs, episodes, blob, new NullJobProgressChannel(), new NullFeedEventChannel(), push, NullLogger<JobCompletionService>.Instance);
        var retry = new JobRetryService(_jobs, episodes, blob, _channel, _speech, new NullJobProgressChannel(), completion, push, new ConfigurationBuilder().Build(), NullLogger<JobRetryService>.Instance);

        return (episodes, completion, retry);
    }

    private async Task<DateTime> SeedEpisodeAsync(EpisodeService episodes, TranscriptStatus? status)
    {
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Retry Podcast", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        var episode = NewEpisode(status);
        await episodes.AddEpisodeFromEntityAsync(episode);
        Directory.CreateDirectory(Path.Combine(_root, FeedId, "audio"));
        await File.WriteAllBytesAsync(Path.Combine(_root, FeedId, "audio", FileName), [1, 2, 3]);

        return episode.UploadedAt;
    }

    private sealed class HookedBlobService(string root) : TestBlobStorageService(root)
    {
        public Action? OnUploadTranscript { get; set; }

        public Action? OnDownloadAudioToTemp { get; set; }

        /// <summary>
        /// Runs once, just before the first delete against the named feed (audio or transcript), or at the end of the test when the
        /// code under test never deletes there, so the upload it simulates happens either way.
        /// </summary>
        public (string FeedId, Action Hook)? BeforeTargetCleanup { get; set; }

        public void RunPendingTargetCleanupHook()
        {
            var pending = BeforeTargetCleanup;
            BeforeTargetCleanup = null;
            pending?.Hook();
        }

        public override Task<bool> DeleteAudioAsync(string feedId, string fileName, string? ifMatchETag = null)
        {
            RunTargetCleanupHook(feedId);

            return base.DeleteAudioAsync(feedId, fileName, ifMatchETag);
        }

        /// <summary>
        /// Runs once, just before the first transcript delete against any feed, with that delete's feed and episode id.
        /// </summary>
        public Action<string, string>? OnDeleteTranscript { get; set; }

        public override Task DeleteTranscriptAsync(string feedId, string episodeId)
        {
            var onDelete = OnDeleteTranscript;
            OnDeleteTranscript = null;
            onDelete?.Invoke(feedId, episodeId);
            RunTargetCleanupHook(feedId);

            return base.DeleteTranscriptAsync(feedId, episodeId);
        }

        private void RunTargetCleanupHook(string feedId)
        {
            if (BeforeTargetCleanup is { } pending && pending.FeedId == feedId)
            {
                RunPendingTargetCleanupHook();
            }
        }

        public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
        {
            OnUploadTranscript?.Invoke();

            return base.UploadTranscriptAsync(feedId, episodeId, vttContent);
        }

        public override async Task<AudioDownload> DownloadAudioToTempAsync(string feedId, string fileName)
        {
            var download = await base.DownloadAudioToTempAsync(feedId, fileName);
            var hook = OnDownloadAudioToTemp;
            OnDownloadAudioToTemp = null;
            hook?.Invoke();

            return download;
        }
    }

    [Fact]
    public async Task UploadJoin_TranscriptWriteFails_CreatesTheEpisodeWithAFailedTranscript()
    {
        // Arrange: the run staged a transcript, but writing it to the episode's location fails
        var blob = new FailingTranscriptBlobService(_root);
        var (episodes, completion, _) = BuildServices(blob);
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Retry Podcast", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        SeedJoinableUploadJob("job-write-fails");
        await blob.UploadPendingTranscriptAsync(FeedId, "job-write-fails", 1, "WEBVTT\n\nstaged");

        // Act
        await completion.TryCompleteJobAsync("job-write-fails", CancellationToken.None);

        // Assert: the episode and the job both report the transcript as failed, so the queue entry offers Retry transcript
        Assert.Equal(TranscriptStatus.Failed, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
        Assert.Equal(EpisodeId, _jobs.GetEntity("job-write-fails")!.EpisodeId);
        Assert.Equal(TranscriptionStatuses.Failed, _jobs.GetEntity("job-write-fails")!.TranscriptionStatus);
        episodes.Dispose();
    }

    [Fact]
    public async Task UploadJoin_StagedTranscriptReadFails_CreatesTheEpisodeWithAFailedTranscript()
    {
        // Arrange: reading the staged transcript fails with a storage error other than not-found
        var blob = new FailingStagedReadBlobService(_root);
        var (episodes, completion, _) = BuildServices(blob);
        await episodes.CreateFeedAsync(new FeedConfig { Id = FeedId, Title = "Retry Podcast", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        SeedJoinableUploadJob("job-read-fails");

        // Act
        await completion.TryCompleteJobAsync("job-read-fails", CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptStatus.Failed, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
        Assert.Equal(TranscriptionStatuses.Failed, _jobs.GetEntity("job-read-fails")!.TranscriptionStatus);
        episodes.Dispose();
    }

    [Fact]
    public async Task MoveEpisode_SourceReuploadedAroundTheSourceTranscriptCleanup_KeepsTheReuploadsTranscript()
    {
        // Arrange: when the move cleans up the source transcript, a re-upload of the same file re-creates the source episode id and
        // publishes its own transcript (on another task, so a move that cleans up inside the episode lock makes it wait)
        var hooked = new HookedBlobService(_root);
        var (episodes, _, _) = BuildServices(hooked);
        await SeedEpisodeAsync(episodes, TranscriptStatus.Available);
        await hooked.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\nsource");
        await episodes.CreateFeedAsync(new FeedConfig { Id = "retry-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        Task? reupload = null;
        hooked.OnDeleteTranscript = (feedId, _) =>
        {
            reupload = Task.Run(() => episodes.AddEpisodeFromEntityAsync(NewEpisode(TranscriptStatus.Available) with { UploadedAt = DateTime.UtcNow.AddSeconds(1) }, CancellationToken.None, "WEBVTT\n\nreuploaded"));

            // A move that cleans up outside the lock lets the re-upload land first; one that holds the lock makes it wait
            reupload.Wait(TimeSpan.FromMilliseconds(500));
        };

        // Act
        await episodes.MoveEpisodeAsync(EpisodeId, FeedId, "retry-target");
        await reupload!;

        // Assert
        Assert.Equal(TranscriptStatus.Available, (await episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
        await using var transcript = await hooked.DownloadTranscriptAsync(FeedId, EpisodeId);
        Assert.NotNull(transcript);
        Assert.Equal("WEBVTT\n\nreuploaded", await new StreamReader(transcript!).ReadToEndAsync());
        episodes.Dispose();
    }

    [Fact]
    public async Task Recovery_TranscriptRetryRecordedButNeverJoined_PublishesWithinOnePass()
    {
        // Arrange: the run recorded its transcript an hour ago, but its join never ran (a stop, or a transient table error)
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Completed;
            e.QueuedAt = DateTimeOffset.UtcNow.AddHours(-1);
        });

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity(jobId)!.Status);
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
    }

    [Theory]
    [InlineData(nameof(JobStatus.Failed))]
    [InlineData(nameof(JobStatus.Cancelled))]
    public async Task TranscriptRetryJoin_SiblingJobThatEndedWithoutPublishing_DoesNotBlockPublication(string siblingStatus)
    {
        // Arrange: a same-file job for the episode ended after the retry was queued, without writing the episode (a failed
        // re-upload, or a failed job a dismissal cancelled)
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        _jobs.SetEntity(new JobStatusEntity { PartitionKey = JobStorageNames.JobsPartitionKey, RowKey = "sibling", FeedId = FeedId, EpisodeId = EpisodeId, Status = siblingStatus, QueuedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert
        Assert.Equal(TranscriptionStatuses.Completed, _jobs.GetEntity(jobId)!.TranscriptionStatus);
        Assert.Equal(TranscriptStatus.Available, (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))?.TranscriptStatus);
    }

    [Fact]
    public async Task TranscriptRetryJoin_EpisodeMovedAwayMidRetry_EndsTerminalWithoutPublishing()
    {
        // Arrange: the retry ran; meanwhile its episode was moved to another feed
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        await _episodes.CreateFeedAsync(new FeedConfig { Id = "retry-target", Title = "Target", Description = "d", Author = "a", Email = "e@example.com", Language = "en", Category = "Technology" });
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nretried");
        await _jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed);
        var moved = await _episodes.MoveEpisodeAsync(EpisodeId, FeedId, "retry-target");

        // Act
        await _completion.TryCompleteJobAsync(jobId, CancellationToken.None);

        // Assert: the job ended, nothing was published at the old id, and the moved episode keeps its failed transcript
        var job = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Null(await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId));
        Assert.Equal(TranscriptStatus.Failed, (await _episodes.GetEpisodeByIdAsync("retry-target", moved.Id))?.TranscriptStatus);
        await using var transcript = await _blob.DownloadTranscriptAsync("retry-target", moved.Id);
        Assert.Null(transcript);
    }

    [Fact]
    public async Task Recovery_ClaimedJoinThatPublishedBeforeAStop_EndsTheJobWithItsPublishedTranscript()
    {
        // Arrange: the join claimed the job and published the transcript, then the server stopped before the terminal write
        await CreateEpisodeAsync(TranscriptStatus.Failed);
        var uploadedAt = (await _episodes.GetEpisodeByIdAsync(FeedId, EpisodeId))!.UploadedAt;
        var jobId = (await _retry.RetryTranscriptAsync(FeedId, EpisodeId, CancellationToken.None)).Job!.JobId;
        await _jobs.MergeJobFieldsAsync(jobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Completed;
            e.JoinClaimedAt = DateTimeOffset.UtcNow;
        });
        Assert.True(await _episodes.PublishTranscriptIfAsync(FeedId, EpisodeId, uploadedAt, () => _blob.UploadTranscriptAsync(FeedId, EpisodeId, "WEBVTT\n\npublished")));

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None);

        // Assert
        var job = _jobs.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Completed, job.TranscriptionStatus);
    }

    [Theory]
    [InlineData(TranscriptionStatuses.Completed)]
    [InlineData(null)]
    public async Task Recovery_RecordedNormalizationWhoseJoinNeverRan_PublishesTheEpisodeOnce(string? transcriptionStatus)
    {
        // Arrange: the App Service merged the normalization result and stopped before the join ended the job
        await CreateFeedAsync();
        SeedJoinableUploadJob("job-unjoined");
        _jobs.GetEntity("job-unjoined")!.TranscriptionStatus = transcriptionStatus;
        await _blob.UploadPendingTranscriptAsync(FeedId, "job-unjoined", 1, "WEBVTT\n\nstaged");

        // Act: two passes, as the recurring recovery would make
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Completed), _jobs.GetEntity("job-unjoined")!.Status);
        Assert.Single(await _episodes.GetAllEpisodesAsync(FeedId));
    }

    [Fact]
    public async Task Recovery_RecordedNormalizationFailureWhoseJoinNeverRan_EndsTheJobFailed()
    {
        // Arrange
        await CreateFeedAsync();
        SeedJoinableUploadJob("job-unjoined-failed");
        var job = _jobs.GetEntity("job-unjoined-failed")!;
        job.NormalizationError = "FFmpeg crashed";
        job.TranscriptionStatus = null;

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Failed), _jobs.GetEntity("job-unjoined-failed")!.Status);
        Assert.Empty(await _episodes.GetAllEpisodesAsync(FeedId));
    }

    [Fact]
    public async Task Recovery_RecordedNormalizationWithTranscriptionStillRunning_LeavesTheJobToItsJoin()
    {
        // Arrange
        await CreateFeedAsync();
        SeedJoinableUploadJob("job-transcribing");
        _jobs.GetEntity("job-transcribing")!.TranscriptionStatus = TranscriptionStatuses.Running;

        // Act
        await _retry.RecoverStalledRetriesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Processing), _jobs.GetEntity("job-transcribing")!.Status);
        Assert.Empty(await _episodes.GetAllEpisodesAsync(FeedId));
    }

    /// <summary>
    /// An upload job whose normalization and transcription both completed, so the next TryCompleteJobAsync runs its join.
    /// </summary>
    private void SeedJoinableUploadJob(string jobId)
    {
        _jobs.SetEntity(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Processing),
            Attempt = 1,
            FeedId = FeedId,
            FileName = FileName,
            EpisodeId = EpisodeId,
            Title = "Upload",
            Source = nameof(UploadSource.Browser),
            OriginalFileSize = 3,
            QueuedAt = DateTimeOffset.UtcNow,
            NormalizationComplete = true,
            NormalizedFileSize = 3,
            TranscriptionStatus = TranscriptionStatuses.Completed,
        });
    }

    private sealed class FailingTranscriptBlobService(string root) : TestBlobStorageService(root)
    {
        public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent) => throw new IOException("blob store unavailable");
    }

    private sealed class FailingStagedReadBlobService(string root) : TestBlobStorageService(root)
    {
        public override Task<string?> DownloadPendingTranscriptAsync(string feedId, string jobId, int attempt) => throw new Azure.RequestFailedException(503, "blob store unavailable");
    }

    // ===== Helpers =====

    private async Task CreateFeedAsync()
    {
        if (await _episodes.GetFeedAsync(FeedId) == null)
        {
            await _episodes.CreateFeedAsync(new FeedConfig
            {
                Id = FeedId,
                Title = "Retry Podcast",
                Description = "Test Description",
                Author = "Test Author",
                Email = "test@example.com",
                Language = "en",
                Category = "Technology"
            });
        }
    }

    private async Task CreateEpisodeAsync(TranscriptStatus? transcriptStatus, bool writeAudio = true)
    {
        await CreateFeedAsync();
        await _episodes.AddEpisodeFromEntityAsync(NewEpisode(transcriptStatus));
        if (writeAudio)
        {
            var audioDir = Path.Combine(_root, FeedId, "audio");
            Directory.CreateDirectory(audioDir);
            await File.WriteAllBytesAsync(Path.Combine(audioDir, FileName), [1, 2, 3]);
        }
    }

    private static Episode NewEpisode(TranscriptStatus? transcriptStatus) => new()
    {
        Id = EpisodeId,
        FeedId = FeedId,
        Title = "Retry Episode",
        FileName = FileName,
        FileSize = 3,
        Duration = TimeSpan.FromMinutes(1),
        PublishedDate = DateTime.UtcNow,
        Source = UploadSource.Browser,
        UploadedAt = DateTime.UtcNow,
        TranscriptStatus = transcriptStatus,
    };

    private JobStatusEntity SeedFailedNormalizationJob(string jobId, bool writePending = true, string? transcriptionStatus = TranscriptionStatuses.Failed)
    {
        var entity = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Failed),
            FeedId = FeedId,
            FileName = FileName,
            EpisodeId = EpisodeId,
            Title = "Retry Episode",
            Source = nameof(UploadSource.Browser),
            OriginalFileSize = 3,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NormalizationStage = nameof(NormalizationStage.Failed),
            NormalizationComplete = true,
            NormalizationError = "FFmpeg crashed",
            Error = "FFmpeg crashed",
            TranscriptionStatus = transcriptionStatus,
        };
        _jobs.SetEntity(entity);

        // A completed transcription has its transcript staged for the attempt that ran it
        if (transcriptionStatus == TranscriptionStatuses.Completed)
        {
            _blob.UploadPendingTranscriptAsync(FeedId, jobId, 1, "WEBVTT\n\nattempt one").GetAwaiter().GetResult();
        }

        if (writePending)
        {
            var pendingDir = Path.Combine(_root, FeedId, "pending", jobId);
            Directory.CreateDirectory(pendingDir);
            File.WriteAllBytes(Path.Combine(pendingDir, FileName), [1, 2, 3]);
        }

        return entity;
    }

    private static NormalizationJob NewMessage(int? attempt) => new()
    {
        JobId = "job-d",
        FeedId = FeedId,
        FileName = FileName,
        OriginalFileSize = 3,
        EpisodeId = EpisodeId,
        Title = "t",
        PublishedDate = DateTime.UtcNow,
        QueuedAt = DateTime.UtcNow,
        Attempt = attempt,
    };

    private async Task<List<TranscriptionRequest>> DrainChannelAsync()
    {
        _channel.Complete();
        List<TranscriptionRequest> requests = [];
        await foreach (var request in _channel.ReadAllAsync(CancellationToken.None))
        {
            requests.Add(request);
        }

        return requests;
    }
}
