using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FeatherPod.Tests;

/// <summary>
/// Runs the <see cref="JobService"/> operations normalization retry relies on against Azurite Table Storage: the conditional
/// replace that resets a job (clearing fields a Merge would skip, and refusing a stale read), and the attempt guards on merges and
/// cancels. The jobs table is shared, so each test uses its own ids.
/// </summary>
[Collection("IntegrationTests")]
public class JobServiceRetryIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [AzuriteFact]
    public async Task TryReplace_ClearsNullFieldsAndRefusesStaleRead()
    {
        // Arrange
        var jobs = await GetJobsAsync();
        var jobId = await CreateFailedAsync(jobs);
        var first = (await jobs.GetJobStatusAsync(jobId))!;
        var stale = (await jobs.GetJobStatusAsync(jobId))!;
        first.Attempt = 2;
        first.Status = nameof(JobStatus.Queued);
        first.NormalizationError = null;
        first.CompletedAt = null;

        // Act
        var replaced = await jobs.TryReplaceAsync(first);
        stale.Attempt = 2;
        var staleReplaced = await jobs.TryReplaceAsync(stale);

        // Assert
        Assert.True(replaced);
        Assert.False(staleReplaced);
        var stored = (await jobs.GetJobStatusAsync(jobId))!;
        Assert.Equal(2, stored.Attempt);
        Assert.Null(stored.NormalizationError);
        Assert.Null(stored.CompletedAt);
    }

    [AzuriteFact]
    public async Task MergeJobFields_WorkOfEarlierAttempt_Refused()
    {
        // Arrange
        var jobs = await GetJobsAsync();
        var jobId = await CreateFailedAsync(jobs);
        var entity = (await jobs.GetJobStatusAsync(jobId))!;
        entity.Attempt = 2;
        entity.Status = nameof(JobStatus.Queued);
        Assert.True(await jobs.TryReplaceAsync(entity));

        // Act
        var stale = await jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Failed, expectedAttempt: 1);
        var current = await jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Running, expectedAttempt: 2);

        // Assert
        Assert.Null(stale);
        Assert.NotNull(current);
        Assert.Equal(TranscriptionStatuses.Running, (await jobs.GetJobStatusAsync(jobId))!.TranscriptionStatus);
    }

    [AzuriteFact]
    public async Task MergeJobFields_PredicateRejectsReadState_WritesNothing()
    {
        // Arrange: a job whose transcription track something else already ended
        var jobs = await GetJobsAsync();
        var jobId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = "rq-feed", FileName = "f.mp3", TranscriptionStatus = TranscriptionStatuses.Queued });
        await jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Failed);

        // Act
        var merged = await jobs.MergeJobFieldsAsync(jobId, e => e.TranscriptionStatus = TranscriptionStatuses.Completed, onlyIf: e => e.TranscriptionStatus is TranscriptionStatuses.Queued or TranscriptionStatuses.Running);

        // Assert
        Assert.Null(merged);
        Assert.Equal(TranscriptionStatuses.Failed, (await jobs.GetJobStatusAsync(jobId))!.TranscriptionStatus);
    }

    [AzuriteFact]
    public async Task CancelJob_ForEarlierAttempt_LeavesRetriedJobRunning()
    {
        // Arrange
        var jobs = await GetJobsAsync();
        var jobId = await CreateFailedAsync(jobs);
        var entity = (await jobs.GetJobStatusAsync(jobId))!;
        entity.Attempt = 2;
        entity.Status = nameof(JobStatus.Queued);
        Assert.True(await jobs.TryReplaceAsync(entity));

        // Act
        var staleCancel = await jobs.CancelJobAsync(jobId, CancellationToken.None, expectedAttempt: 1);

        // Assert
        Assert.Null(staleCancel);
        Assert.Equal(nameof(JobStatus.Queued), (await jobs.GetJobStatusAsync(jobId))!.Status);
    }

    [AzuriteFact]
    public async Task CancelJob_TranscriptRetryWhoseJoinClaimedIt_Refused()
    {
        // Arrange: a transcript retry whose join has claimed it and is publishing
        var jobs = await GetJobsAsync();
        var jobId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = "rq-feed", FileName = "f.mp3", EpisodeId = "ep-" + jobId, Kind = JobKinds.TranscriptRetry, TranscriptionStatus = TranscriptionStatuses.Completed });
        await jobs.MergeJobFieldsAsync(jobId, e => e.JoinClaimedAt = DateTimeOffset.UtcNow);

        // Act
        var cancelled = await jobs.CancelJobAsync(jobId);

        // Assert
        Assert.Null(cancelled);
        Assert.NotEqual(nameof(JobStatus.Cancelled), (await jobs.GetJobStatusAsync(jobId))!.Status);
    }

    [AzuriteFact]
    public async Task CancelJob_UploadWhoseJoinClaimedIt_Refused()
    {
        // Arrange: an upload job whose join has claimed it and is publishing its episode
        var jobs = await GetJobsAsync();
        var jobId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = "rq-feed", FileName = "f.mp3", EpisodeId = "ep-" + jobId, TranscriptionStatus = TranscriptionStatuses.Completed });
        await jobs.MergeJobFieldsAsync(jobId, e =>
        {
            // The row as the writers leave it: the Function moved it to Processing and signalled, the join's claim recorded its incarnation
            var claimedAt = DateTimeOffset.UtcNow;
            e.Status = nameof(JobStatus.Processing);
            e.NormalizationComplete = true;
            e.JoinClaimedAt = claimedAt;
            e.EpisodeUploadedAt = claimedAt;
            e.PublishedTitle = "f";
        });

        // Act
        var cancelled = await jobs.CancelJobAsync(jobId);

        // Assert
        Assert.Null(cancelled);
        Assert.NotEqual(nameof(JobStatus.Cancelled), (await jobs.GetJobStatusAsync(jobId))!.Status);
    }

    [AzuriteFact]
    public async Task CancelJob_UploadWhoseClaimedJoinEndedItFailed_IsCancelled()
    {
        // Arrange: a join claimed the upload and could not publish its episode, so it ended the job Failed with the claim on record
        var jobs = await GetJobsAsync();
        var jobId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = "rq-feed", FileName = "f.mp3", EpisodeId = "ep-" + jobId, TranscriptionStatus = TranscriptionStatuses.Completed });
        await jobs.MergeJobFieldsAsync(jobId, e =>
        {
            var claimedAt = DateTimeOffset.UtcNow;
            e.Status = nameof(JobStatus.Processing);
            e.NormalizationComplete = true;
            e.JoinClaimedAt = claimedAt;
            e.EpisodeUploadedAt = claimedAt;
            e.PublishedTitle = "f";
        });
        var claimed = (await jobs.GetJobStatusAsync(jobId))!;
        claimed.Status = nameof(JobStatus.Failed);
        claimed.Error = JobCompletionService.EpisodeNotPublishedError;
        claimed.CompletedAt = DateTimeOffset.UtcNow;
        claimed.EpisodeId = null;
        Assert.True(await jobs.TryReplaceAsync(claimed));
        Assert.Null((await jobs.GetJobStatusAsync(jobId))!.EpisodeId);

        // Act
        var cancelled = await jobs.CancelJobAsync(jobId);

        // Assert
        Assert.NotNull(cancelled);
        Assert.Equal(nameof(JobStatus.Cancelled), (await jobs.GetJobStatusAsync(jobId))!.Status);
    }

    [AzuriteFact]
    public async Task GetActiveJobs_ReturnsNonTerminalJobsAcrossFeeds()
    {
        // Arrange
        var jobs = await GetJobsAsync();
        var activeId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = activeId, FeedId = "rq-other-feed", FileName = "a.mp3" });
        var failedId = await CreateFailedAsync(jobs);

        // Act
        var active = await jobs.GetActiveJobsAsync();

        // Assert
        Assert.Contains(activeId, active.Select(j => j.RowKey));
        Assert.DoesNotContain(failedId, active.Select(j => j.RowKey));
    }

    private async Task<IJobService> GetJobsAsync()
    {
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        await jobs.InitializeAsync();

        return jobs;
    }

    private static async Task<string> CreateFailedAsync(IJobService jobs)
    {
        var jobId = "rq-" + Guid.NewGuid().ToString("N")[..8];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = "rq-feed", FileName = "f.mp3", EpisodeId = "ep-" + jobId });
        await jobs.UpdateJobStatusAsync(jobId, e =>
        {
            e.Status = nameof(JobStatus.Failed);
            e.NormalizationComplete = true;
            e.NormalizationError = "boom";
            e.CompletedAt = DateTimeOffset.UtcNow;
        });

        return jobId;
    }
}
