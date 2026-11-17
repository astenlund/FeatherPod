using System.Collections.Concurrent;

using FeatherPod.Shared;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// The outcome of a retry request: <see cref="JobRetryOutcome.Accepted"/> with the job that now runs, or the reason it was refused.
/// </summary>
public sealed record JobRetryResult(JobRetryOutcome Outcome, JobStatusResponse? Job = null);

/// <summary>
/// Why a retry request was accepted or refused. Each refusal is a distinct reason so callers (the push page, the CLI) can react to it.
/// </summary>
public enum JobRetryOutcome
{
    Accepted,

    /// <summary>The episode (transcript retry) or job (normalization retry) does not exist.</summary>
    NotFound,

    /// <summary>The episode's transcript did not fail, or the job did not fail in normalization.</summary>
    NotRetryable,

    /// <summary>Speech transcription is not configured on this server (transcript retry).</summary>
    SpeechUnavailable,

    /// <summary>The episode's published audio blob is missing (transcript retry).</summary>
    AudioMissing,

    /// <summary>Another transcript retry of the same episode is still active.</summary>
    RetryActive,

    /// <summary>The job's pending source blob is gone, so only a re-upload can retry it (normalization retry).</summary>
    SourceGone,

    /// <summary>The job changed while it was being reset, as when a concurrent retry of the same job won (normalization retry).</summary>
    Conflict,

    /// <summary>The re-queue failed; the job was returned to its failed state and can be retried again (normalization retry).</summary>
    QueueFailed,
}

/// <summary>
/// Retries a failed transcription or normalization without re-uploading the source audio, and runs the recovery that keeps a retry
/// from stranding a job (<see cref="RecoverStalledRetriesAsync"/>, driven by <see cref="JobRecoveryService"/>).
///
/// <para>
/// A transcript retry never reopens the job that published the episode: it creates a new transcription-only job
/// (<see cref="JobKinds.TranscriptRetry"/>) bound to the episode, reading the published audio. A normalization retry resets the failed
/// job in place under a new attempt (<see cref="JobStatusEntity.Attempt"/>) and re-queues it; work of an earlier attempt then changes
/// nothing. The reset is persisted before the message is sent, a failed send returns the job to its failed state, and the recurring
/// recovery returns a reset attempt that no run picked up to its failed state, so every partial state ends in a live run or a job that
/// can be retried again.
/// </para>
/// </summary>
public sealed class JobRetryService
{
    /// <summary>Error recorded on a job whose normalization retry did not start; the user can retry again.</summary>
    internal const string RetryNotStartedError = "Normalization retry did not start; retry again";

    /// <summary>Transcription error recorded on a transcript retry job the recurring recovery ended.</summary>
    internal const string TranscriptRetryStalledError = "Transcript retry stalled";

    /// <summary>Transcription error recorded when a normalization retry finds a leftover transcription it cannot re-run.</summary>
    internal const string TranscriptionNotRerunError = "Transcription did not finish and Speech is not configured";

    private readonly IJobService _jobService;
    private readonly EpisodeService _episodeService;
    private readonly IBlobStorageService _blobService;
    private readonly ITranscriptionChannel _transcriptionChannel;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly JobCompletionService _completionService;
    private readonly PushNotificationService _pushNotificationService;
    private readonly ILogger<JobRetryService> _logger;
    private readonly TimeSpan _unpickedRetryBound;
    private readonly TimeSpan _stalledTranscriptRetryBound;

    /// <summary>
    /// Episodes whose transcript retry job is being created. The server keeps its episodes in one process (EpisodeService), so claiming
    /// an episode here before the active-job check makes the one-in-flight limit hold under concurrent requests.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _transcriptRetryClaims = new(StringComparer.Ordinal);

    public JobRetryService(
        IJobService jobService,
        EpisodeService episodeService,
        IBlobStorageService blobService,
        ITranscriptionChannel transcriptionChannel,
        ISpeechTranscriptionService speechService,
        IJobProgressChannel progressChannel,
        JobCompletionService completionService,
        PushNotificationService pushNotificationService,
        IConfiguration configuration,
        ILogger<JobRetryService> logger)
    {
        _pushNotificationService = pushNotificationService;
        _jobService = jobService;
        _episodeService = episodeService;
        _blobService = blobService;
        _transcriptionChannel = transcriptionChannel;
        _speechService = speechService;
        _progressChannel = progressChannel;
        _completionService = completionService;
        _logger = logger;

        // Longer than any legitimate queue wait: each Function instance takes one message at a time and may run it for an hour
        _unpickedRetryBound = TimeSpan.FromMinutes(configuration.GetValue("JobRecovery:UnpickedRetryMinutes", 360));

        // Longer than any legitimate transcription, matching CleanupFunction's stale-transcription rule
        _stalledTranscriptRetryBound = TimeSpan.FromHours(configuration.GetValue("JobRecovery:StalledTranscriptRetryHours", 24));
    }

    /// <summary>
    /// Start a transcript retry of an episode whose transcript failed: a new transcription-only job bound to the episode.
    /// </summary>
    public async Task<JobRetryResult> RetryTranscriptAsync(string feedId, string episodeId, CancellationToken ct)
    {
        var episode = await _episodeService.GetEpisodeByIdAsync(feedId, episodeId);
        if (episode == null)
        {
            return new(JobRetryOutcome.NotFound);
        }

        if (episode.TranscriptStatus != TranscriptStatus.Failed)
        {
            return new(JobRetryOutcome.NotRetryable);
        }

        if (!_speechService.IsAvailable)
        {
            return new(JobRetryOutcome.SpeechUnavailable);
        }

        if (!await _blobService.AudioExistsAsync(feedId, episode.FileName))
        {
            return new(JobRetryOutcome.AudioMissing);
        }

        var claimKey = $"{feedId}/{episodeId}";
        if (!_transcriptRetryClaims.TryAdd(claimKey, 0))
        {
            return new(JobRetryOutcome.RetryActive);
        }

        try
        {
            var activeJobs = await _jobService.GetActiveJobsByFeedAsync(feedId, ct);
            if (activeJobs.Any(j => JobKinds.IsTranscriptRetry(j.Kind) && j.EpisodeId == episodeId))
            {
                return new(JobRetryOutcome.RetryActive);
            }

            // Eligibility decided again under the claim, after the active-jobs check: the reads above may predate another retry that
            // has since published (a join publishes before it ends its job, so a retry no longer listed active has already published),
            // or a re-created episode. The job records this read's incarnation.
            episode = await _episodeService.GetEpisodeByIdAsync(feedId, episodeId);
            if (episode == null)
            {
                return new(JobRetryOutcome.NotFound);
            }

            if (episode.TranscriptStatus != TranscriptStatus.Failed)
            {
                return new(JobRetryOutcome.NotRetryable);
            }

            var jobId = Guid.NewGuid().ToString("N");
            await _jobService.CreateJobStatusAsync(new CreateJobOptions
            {
                JobId = jobId,
                FeedId = feedId,
                FileName = episode.FileName,
                Title = episode.Title,
                EpisodeId = episodeId,
                Source = episode.Source.ToString(),
                TranscriptionStatus = TranscriptionStatuses.Queued,
                Kind = JobKinds.TranscriptRetry,
                EpisodeUploadedAt = new DateTimeOffset(DateTime.SpecifyKind(episode.UploadedAt, DateTimeKind.Utc)),
            }, CancellationToken.None);

            await _transcriptionChannel.SubmitAsync(new TranscriptionRequest
            {
                JobId = jobId,
                FeedId = feedId,
                FileName = episode.FileName,
                EpisodeId = episodeId,
                Attempt = 1,
                FromEpisodeAudio = true,
            }, CancellationToken.None);

            _logger.LogInformation("Started transcript retry job {JobId} for episode {EpisodeId} in feed {FeedId}", jobId, episodeId, feedId);
            var created = await _jobService.GetJobStatusAsync(jobId, CancellationToken.None);

            return new(JobRetryOutcome.Accepted, created == null ? null : JobStatusResponse.FromEntity(created));
        }
        finally
        {
            _transcriptRetryClaims.TryRemove(claimKey, out _);
        }
    }

    /// <summary>
    /// Retry a job whose normalization failed: reset it in place under a new attempt and re-queue it, while its pending source exists.
    /// With <paramref name="expectedAttempt"/> (the attempt the caller's decision was made on), a job that has moved to another attempt
    /// is refused as a conflict; the check reads the same entity whose ETag the reset is conditional on.
    /// </summary>
    public async Task<JobRetryResult> RetryNormalizationAsync(JobStatusEntity job, CancellationToken ct, int? expectedAttempt = null)
    {
        if (expectedAttempt is { } expected && expected != job.GetAttempt())
        {
            return new(JobRetryOutcome.Conflict);
        }

        if (job.GetJobStatus() != JobStatus.Failed || string.IsNullOrEmpty(job.NormalizationError) || JobKinds.IsTranscriptRetry(job.Kind))
        {
            return new(JobRetryOutcome.NotRetryable);
        }

        if (job.FeedId == null || job.FileName == null || job.EpisodeId == null)
        {
            return new(JobRetryOutcome.NotRetryable);
        }

        // The cleanup claims a terminal job's source on the job row before deleting it, so a claimed source is gone or about to be, and
        // a claim made after this read changes the ETag the reset below is conditional on
        if (job.SourceSweptAt != null || !await _blobService.PendingBlobExistsAsync(job.FeedId, job.RowKey, job.FileName))
        {
            return new(JobRetryOutcome.SourceGone);
        }

        // A completed transcription is kept only when its staged transcript can move to the new attempt (the join reads the current
        // attempt's); otherwise it is run again like any unfinished one
        string? keptTranscript = null;
        if (job.TranscriptionStatus == TranscriptionStatuses.Completed)
        {
            keptTranscript = await StagedTranscripts.ReadCompletedAsync(_blobService, job);
            if (keptTranscript == null)
            {
                job.TranscriptionStatus = TranscriptionStatuses.Failed;
            }
        }

        var originalError = job.NormalizationError;
        var rerunTranscription = ResetForNormalizationRetry(job, _speechService.IsAvailable, DateTimeOffset.UtcNow);
        if (keptTranscript != null)
        {
            await _blobService.UploadPendingTranscriptAsync(job.FeedId, job.RowKey, job.GetAttempt(), keptTranscript);
        }

        // Conditional on the ETag of the read that found the job failed, so of concurrent retries exactly one resets it
        if (!await _jobService.TryReplaceAsync(job, CancellationToken.None))
        {
            return new(JobRetryOutcome.Conflict);
        }

        try
        {
            await _jobService.QueueNormalizationJobAsync(BuildNormalizationMessage(job), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-queue of normalization retry for job {JobId} failed; returning the job to its failed state", job.RowKey);
            await ReturnUnstartedRetryToFailedAsync(job, originalError ?? RetryNotStartedError, CancellationToken.None);

            return new(JobRetryOutcome.QueueFailed);
        }

        if (rerunTranscription)
        {
            await _transcriptionChannel.SubmitAsync(new TranscriptionRequest
            {
                JobId = job.RowKey,
                FeedId = job.FeedId,
                FileName = job.FileName,
                EpisodeId = job.EpisodeId,
                Attempt = job.GetAttempt(),
            }, CancellationToken.None);
        }

        _logger.LogInformation("Started normalization retry of job {JobId} (attempt {Attempt})", job.RowKey, job.GetAttempt());
        var response = JobStatusResponse.FromEntity(job);
        _progressChannel.Publish(job.RowKey, response);

        return new(JobRetryOutcome.Accepted, response);
    }

    /// <summary>
    /// The recurring recovery: returns a normalization retry that no run picked up within its bound to its failed state (a send that
    /// failed and whose compensating write also failed, or a server stop between the reset and the send), and ends a transcript retry
    /// job that stopped making progress, so its episode's one-in-flight limit cannot outlive it, and runs the join of a job whose join
    /// inputs are all recorded but which was never joined (<see cref="IsUnjoinedRecordedJob"/> holds the exact condition). A failed scan
    /// is logged.
    /// </summary>
    public async Task RecoverStalledRetriesAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<JobStatusEntity> active;
        try
        {
            active = await _jobService.GetActiveJobsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retry recovery failed to scan active jobs");

            return;
        }

        foreach (var job in active)
        {
            try
            {
                if (IsUnpickedNormalizationRetry(job, now, _unpickedRetryBound))
                {
                    _logger.LogWarning("Normalization retry of job {JobId} (attempt {Attempt}) was not picked up; returning it to its failed state", job.RowKey, job.GetAttempt());
                    await ReturnUnstartedRetryToFailedAsync(job, RetryNotStartedError, ct);
                }
                else if (IsStalledTranscriptRetry(job, now, _stalledTranscriptRetryBound))
                {
                    _logger.LogWarning("Transcript retry job {JobId} stalled; ending it", job.RowKey);
                    await EndStalledTranscriptRetryAsync(job, ct);
                }
                else if (IsUnjoinedRecordedJob(job))
                {
                    // Its join is idempotent and claims the terminal status conditionally, so a join running now is unaffected
                    _logger.LogWarning("Job {JobId} has both tracks recorded but was never joined; running its join", job.RowKey);
                    await _completionService.TryCompleteJobAsync(job.RowKey, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry recovery failed for job {JobId}", job.RowKey);
            }
        }
    }

    /// <summary>
    /// Whether a job is still active although everything its join waits on is recorded, so its join never ran. For an upload job:
    /// its normalization result is merged and its transcription track is ended or absent (the App Service stopped between merging
    /// the result and the join's terminal write; the Function no longer redelivers such a message, since its fallback leaves a
    /// recorded result alone). For a transcript retry: its run recorded its track but no join claimed the job (a stop, or a transient
    /// table error in the join). The recurring recovery runs the join for it; a join running at the same time wins or loses the
    /// same conditional claim, so only one publishes.
    /// </summary>
    internal static bool IsUnjoinedRecordedJob(JobStatusEntity job) =>
        JobKinds.IsTranscriptRetry(job.Kind)
            ? job.JoinClaimedAt == null && job.TranscriptionStatus is TranscriptionStatuses.Completed or TranscriptionStatuses.Failed
            : job.NormalizationComplete == true && job.TranscriptionStatus is null or TranscriptionStatuses.Completed or TranscriptionStatuses.Failed;

    /// <summary>
    /// Whether a job is a normalization retry that no run picked up within <paramref name="bound"/>: a reset attempt (attempt 2 or
    /// later) still queued, its normalization not started, queued longer ago than the bound.
    /// </summary>
    internal static bool IsUnpickedNormalizationRetry(JobStatusEntity job, DateTimeOffset now, TimeSpan bound) =>
        !JobKinds.IsTranscriptRetry(job.Kind) &&
        job.GetAttempt() > 1 &&
        job.GetJobStatus() == JobStatus.Queued &&
        job.NormalizationStage == nameof(NormalizationStage.Queued) &&
        job.QueuedAt is { } queuedAt && now - queuedAt > bound;

    /// <summary>
    /// Whether a job is a transcript retry still active longer than <paramref name="bound"/> after it was queued.
    /// </summary>
    internal static bool IsStalledTranscriptRetry(JobStatusEntity job, DateTimeOffset now, TimeSpan bound) =>
        JobKinds.IsTranscriptRetry(job.Kind) &&
        !job.GetJobStatus().IsTerminal() &&
        job.QueuedAt is { } queuedAt && now - queuedAt > bound;

    /// <summary>
    /// Reset a failed job for a normalization retry, in place: the next attempt, a new queue time and no completion time (so every
    /// time-window consumer treats it as new), normalization result and errors cleared. The transcription track is kept when it
    /// completed; otherwise it is queued again when Speech is configured, and without Speech a transcription that was attempted but did
    /// not finish ends failed while one never attempted stays not attempted. Returns whether transcription must be re-run.
    /// </summary>
    internal static bool ResetForNormalizationRetry(JobStatusEntity job, bool speechAvailable, DateTimeOffset now)
    {
        job.Attempt = job.GetAttempt() + 1;
        job.SetJobStatus(JobStatus.Queued);
        job.QueuedAt = now;
        job.CompletedAt = null;
        job.Error = null;
        job.NormalizationStage = nameof(NormalizationStage.Queued);
        job.NormalizationProgress = 0;
        job.ProgressMessage = "Waiting in queue";
        job.NormalizationComplete = null;
        job.NormalizationError = null;
        job.NormalizedFileSize = null;
        job.AudioDurationMs = null;
        job.CurrentPositionMs = null;
        job.TotalDurationMs = null;

        if (job.TranscriptionStatus == TranscriptionStatuses.Completed)
        {
            return false;
        }

        if (speechAvailable)
        {
            job.TranscriptionStatus = TranscriptionStatuses.Queued;
            job.TranscriptionError = null;
            job.TranscriptionStartedAt = null;

            return true;
        }

        if (job.TranscriptionStatus is TranscriptionStatuses.Queued or TranscriptionStatuses.Running)
        {
            job.TranscriptionStatus = TranscriptionStatuses.Failed;
            job.TranscriptionError = TranscriptionNotRerunError;
        }

        return false;
    }

    private static NormalizationJob BuildNormalizationMessage(JobStatusEntity job) => new()
    {
        JobId = job.RowKey,
        FeedId = job.FeedId!,
        FileName = job.FileName!,
        OriginalFileSize = job.OriginalFileSize ?? 0,
        EpisodeId = job.EpisodeId!,
        Title = job.Title ?? EpisodeService.ParseTitleFromFilename(job.FileName!),
        Description = job.Description,
        Summary = job.Summary,
        PublishedDate = job.PublishedDate?.UtcDateTime ?? DateTime.UtcNow,
        QueuedAt = job.QueuedAt?.UtcDateTime ?? DateTime.UtcNow,
        Source = Enum.TryParse<UploadSource>(job.Source, out var source) ? source : UploadSource.CLI,
        ProgressMode = job.ProgressMode,
        ProgressIntervalMs = job.ProgressIntervalMs,
        Attempt = job.GetAttempt(),
    };

    /// <summary>
    /// Return a reset attempt that no run picked up to its failed state, still under that attempt so no late message of it can run, and
    /// only while the job is still queued on that attempt with its normalization not started.
    /// </summary>
    private async Task ReturnUnstartedRetryToFailedAsync(JobStatusEntity job, string error, CancellationToken ct)
    {
        var current = await _jobService.GetJobStatusAsync(job.RowKey, ct);
        if (current == null || current.GetAttempt() != job.GetAttempt() || current.GetJobStatus() != JobStatus.Queued ||
            current.NormalizationStage != nameof(NormalizationStage.Queued))
        {
            return;
        }

        try
        {
            await _jobService.MergeWithETagAsync(current.RowKey, e =>
            {
                e.Status = nameof(JobStatus.Failed);
                e.NormalizationComplete = true;
                e.NormalizationError = error;
                e.Error = error;
                e.CompletedAt = DateTimeOffset.UtcNow;
            }, current.ETag, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 412)
        {
            // A run picked the attempt up meanwhile
            return;
        }

        // A terminal status ingestion point: progress streams and the terminal push notification learn of the failed retry
        if (await _jobService.GetJobStatusAsync(current.RowKey, ct) is { } failed)
        {
            var response = JobStatusResponse.FromEntity(failed);
            _progressChannel.Publish(failed.RowKey, response);
            _pushNotificationService.TryNotifyJobTerminal(response);
        }
    }

    /// <summary>
    /// End a stalled transcript retry finally: its transcription fails and its join completes it, leaving the episode's transcript
    /// failed so it can be retried again. A run of it that finishes later finds the job terminal and records nothing.
    /// </summary>
    private async Task EndStalledTranscriptRetryAsync(JobStatusEntity job, CancellationToken ct)
    {
        // A join that claimed the job and stopped before ending it: end it now, reporting what the stopped join persisted. Its publish
        // landed exactly when the episode is still the incarnation the retry started on and has an available transcript: only a publish
        // turns that incarnation from failed to available, a re-creation gets a new incarnation, and no other retry of the episode runs
        // while this job is active. A join still running in this process owns the job until it writes the terminal status itself, so it
        // is left to the next pass.
        if (job.JoinClaimedAt != null)
        {
            if (_completionService.IsTranscriptRetryJoinLive(job.RowKey))
            {
                return;
            }

            var published = job.EpisodeUploadedAt is { } incarnation &&
                await _episodeService.HasAvailableTranscriptAsync(job.FeedId!, job.EpisodeId!, incarnation.UtcDateTime);
            if (await _completionService.TryEndTranscriptRetryAsync(job.RowKey, job.TranscriptionStatus == TranscriptionStatuses.Completed, published, ct) &&
                await _jobService.GetJobStatusAsync(job.RowKey, ct) is { } ended)
            {
                var response = JobStatusResponse.FromEntity(ended);
                _progressChannel.Publish(ended.RowKey, response);
                _pushNotificationService.TryNotifyJobTerminal(response);
            }

            return;
        }

        // A run that finished but whose join never ran (a stop between them) only needs its join, which publishes and ends the job
        if (job.TranscriptionStatus == TranscriptionStatuses.Completed)
        {
            await _completionService.TryCompleteJobAsync(job.RowKey, ct);

            return;
        }

        try
        {
            await _jobService.MergeWithETagAsync(job.RowKey, e =>
            {
                e.TranscriptionStatus = TranscriptionStatuses.Failed;
                e.TranscriptionError = TranscriptRetryStalledError;
            }, job.ETag, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 412)
        {
            // The run moved the job meanwhile; the next pass judges it again
            return;
        }

        await _completionService.TryCompleteJobAsync(job.RowKey, ct);
    }
}
