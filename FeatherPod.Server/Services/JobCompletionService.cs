using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Centralized join point for the fork-join pipeline.
/// When both normalization and transcription tracks are terminal, creates the episode.
/// Implements read-after-write pattern with CAS guard for concurrency safety.
/// </summary>
public class JobCompletionService
{
    private readonly IJobService _jobService;
    private readonly EpisodeService _episodeService;
    private readonly IBlobStorageService _blobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly PushNotificationService _pushNotificationService;
    private readonly ILogger<JobCompletionService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _liveJoins = new(StringComparer.Ordinal);

    public JobCompletionService(
        IJobService jobService,
        EpisodeService episodeService,
        IBlobStorageService blobService,
        IJobProgressChannel progressChannel,
        IFeedEventChannel feedEventChannel,
        PushNotificationService pushNotificationService,
        ILogger<JobCompletionService> logger)
    {
        _jobService = jobService;
        _episodeService = episodeService;
        _blobService = blobService;
        _progressChannel = progressChannel;
        _feedEventChannel = feedEventChannel;
        _pushNotificationService = pushNotificationService;
        _logger = logger;
    }

    /// <summary>
    /// Called by normalization-complete endpoint when Function finishes.
    /// Writes normalization results to entity, then checks join.
    /// </summary>
    public async Task HandleNormalizationCompleteAsync(string jobId, NormalizationCompleteRequest request, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var entity = await _jobService.GetJobStatusAsync(jobId, ct);
            if (entity == null)
            {
                _logger.LogWarning("Job {JobId} not found for normalization-complete", jobId);

                return;
            }

            if (!entity.IsCurrentAttempt(request.Attempt))
            {
                _logger.LogInformation("Ignoring normalization-complete for job {JobId}: signal of attempt {Attempt}, job is on attempt {Current}", jobId, request.Attempt ?? 1, entity.GetAttempt());

                return;
            }

            if (entity.NormalizationComplete == true)
            {
                _logger.LogDebug("Normalization already marked complete for job {JobId}, skipping", jobId);

                return;
            }

            try
            {
                await _jobService.MergeWithETagAsync(jobId, e =>
                {
                    e.NormalizationComplete = true;
                    if (request.Success)
                    {
                        e.NormalizedFileSize = request.NormalizedFileSize;
                        e.AudioETag = request.AudioETag;
                        e.AudioDurationMs = request.AudioDurationMs;
                    }
                    else
                    {
                        e.NormalizationError = request.Error;
                        e.Error = request.Error;
                    }
                }, entity.ETag, ct);

                break;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412 && attempt < maxRetries)
            {
                _logger.LogDebug("ETag conflict writing normalization-complete for job {JobId}, attempt {Attempt}", jobId, attempt);
            }
        }

        await TryCompleteJobAsync(jobId, ct);
    }

    /// <summary>
    /// Check if both tracks are terminal. If so, create episode.
    /// Called after either track writes its terminal status.
    /// </summary>
    public async Task TryCompleteJobAsync(string jobId, CancellationToken ct)
    {
        var entity = await _jobService.GetJobStatusAsync(jobId, ct);
        if (entity == null)
        {
            _logger.LogWarning("Job {JobId} not found for completion check", jobId);

            return;
        }

        // Already completed by another caller
        if (entity.GetJobStatus().IsTerminal())
        {
            _logger.LogDebug("Job {JobId} already in terminal state {Status}", jobId, entity.Status);

            return;
        }

        // Check if both tracks are terminal
        if (entity.NormalizationComplete != true)
        {
            return;
        }

        // Transcription: null = disabled (terminal for join), Completed/Failed = terminal
        var transcriptionTerminal = entity.TranscriptionStatus is null or TranscriptionStatuses.Completed or TranscriptionStatuses.Failed;
        if (!transcriptionTerminal)
        {
            return;
        }

        if (JobKinds.IsTranscriptRetry(entity.Kind))
        {
            await CompleteTranscriptRetryAsync(entity, ct);

            return;
        }

        // Normalization failed: no episode created, job is Failed
        if (!string.IsNullOrEmpty(entity.NormalizationError))
        {
            if (await TrySetTerminalStatusAsync(entity, nameof(JobStatus.Failed), entity.NormalizationError, ct) is null)
            {
                return;
            }

            _logger.LogWarning("Normalization failed for job {JobId}, no episode created", jobId);
            PublishTerminalProgress(jobId, await _jobService.GetJobStatusAsync(jobId, ct));

            return;
        }

        await CompleteUploadAsync(entity, ct);
    }

    /// <summary>
    /// Join of an upload job whose normalization succeeded, written so that no reader sees a terminal state that does not match what
    /// was published (the transcript retry join's sequence): it claims the job with <see cref="JobStatusEntity.JoinClaimedAt"/>, a
    /// conditional write that leaves the job active and records the episode incarnation it will publish
    /// (<see cref="JobStatusEntity.EpisodeUploadedAt"/>), publishes the episode with its transcript, and only then writes the single
    /// terminal status: Completed, its transcription Failed when the completed transcript could not be published, or Failed
    /// (<see cref="EpisodeNotPublishedError"/>, the planned episode id removed) when the episode could not be created. A join that finds
    /// the claim returns; one that stops after claiming leaves an active claimed job that the recurring recovery ends
    /// (<see cref="EndStoppedUploadJoinAsync"/>). From its claim write on, the join runs to its terminal write whatever the caller's
    /// cancellation (an aborted signal request), since a join stopped before publishing would end an upload whose tracks both succeeded
    /// as Failed; <paramref name="ct"/> is not used past the decision to join.
    /// </summary>
    private async Task CompleteUploadAsync(JobStatusEntity entity, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        if (entity.JoinClaimedAt != null)
        {
            return;
        }

        // Registered before the claim write, so recovery never sees the claim without the live mark; removed once this join has ended
        if (!_liveJoins.TryAdd(jobId, 0))
        {
            return;
        }

        try
        {
            // The claim is the first step that cannot be undone, so it ignores the caller's cancellation too: a claim the table committed
            // while the caller observed a cancellation would leave a claimed job no join publishes
            if (await TryClaimUploadJoinAsync(entity, CancellationToken.None) is not { } claimed)
            {
                return;
            }

            _logger.LogInformation("Both tracks terminal for job {JobId}, creating episode", jobId);
            var published = await PublishUploadedEpisodeAsync(claimed, CancellationToken.None);
            if (await TryEndUploadJoinAsync(jobId, claimed.GetAttempt(), published, claimed.PublishedTitle, CancellationToken.None) is not { } ended)
            {
                return;
            }

            await FinishUploadJoinAsync(claimed, ended);
        }
        finally
        {
            _liveJoins.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Claim an upload job for its join: a conditional write of <see cref="JobStatusEntity.JoinClaimedAt"/>, the episode id, the upload
    /// time and the title the episode will be published with, leaving the job active. After any failure of the write the job is re-read:
    /// a row carrying a write token this invocation issued means one of its writes committed although its outcome was lost (an SDK
    /// replay of a committed write answers 412, and an earlier write can commit after a retry began), and the claim stands; otherwise a
    /// conflicting write (a deferred title, say) is retried while the job is still unclaimed, active and on the same attempt. Returns
    /// the claimed job, or null when another join claimed it, it moved on or the write kept failing.
    /// </summary>
    private async Task<JobStatusEntity?> TryClaimUploadJoinAsync(JobStatusEntity entity, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        var decidedAttempt = entity.GetAttempt();

        // Every write token this invocation issued: a failed write can still be pending and commit after a retry began, and a random
        // token is never issued by another join, in this process or another
        var issued = new List<string>();
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var fileSize = entity.NormalizedFileSize ?? entity.OriginalFileSize ?? 0;
            var episodeId = entity.EpisodeId ?? Episode.GenerateId(entity.FeedId!, entity.FileName!, fileSize);
            var title = entity.Title ?? entity.FileName ?? "Untitled";
            var claimedAt = NextClaimStamp();
            var token = NewJoinWriteToken();
            issued.Add(token);
            try
            {
                await _jobService.MergeWithETagAsync(jobId, e =>
                {
                    e.JoinClaimedAt = claimedAt;
                    e.EpisodeId = episodeId;
                    e.EpisodeUploadedAt = claimedAt;
                    e.PublishedTitle = title;
                    e.JoinWriteToken = token;
                }, entity.ETag, ct);

                entity.JoinClaimedAt = claimedAt;
                entity.EpisodeId = episodeId;
                entity.EpisodeUploadedAt = claimedAt;
                entity.PublishedTitle = title;
                entity.JoinWriteToken = token;

                return entity;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var reread = await _jobService.GetJobStatusAsync(jobId, ct);
                if (reread is { JoinClaimedAt: not null, JoinWriteToken: { } written } && issued.Contains(written) && !reread.GetJobStatus().IsTerminal() && reread.GetAttempt() == decidedAttempt)
                {
                    _logger.LogInformation(ex, "The claim write of job {JobId} failed after it committed; the join goes on", jobId);

                    return reread;
                }

                if (reread == null || reread.JoinClaimedAt != null || reread.GetJobStatus().IsTerminal() || reread.GetAttempt() != decidedAttempt)
                {
                    _logger.LogDebug("Job {JobId} was claimed by another join, ended or moved to another attempt", jobId);

                    return null;
                }

                if (attempt >= maxRetries)
                {
                    _logger.LogWarning("Failed to claim job {JobId} for its join after {MaxRetries} attempts; the recurring recovery joins it", jobId, maxRetries);

                    return null;
                }

                entity = reread;
            }
        }

        return null;
    }

    private static long _lastClaimStampTicks;

    /// <summary>A new <see cref="JobStatusEntity.JoinWriteToken"/>: random, so no other join, in this process or another, issues it.</summary>
    private static string NewJoinWriteToken() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// A claim's incarnation stamp: the current time in whole milliseconds (so it survives the table and JSON round trips unchanged), and
    /// later than every stamp issued before it in this process, so two jobs bound to the same episode id claimed by this process never
    /// record the same incarnation. Across processes only the clock separates stamps. A join recognizes its own writes by their
    /// <see cref="JobStatusEntity.JoinWriteToken"/>, never by this stamp.
    /// </summary>
    internal static DateTimeOffset NextClaimStamp()
    {
        while (true)
        {
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            nowTicks -= nowTicks % TimeSpan.TicksPerMillisecond;
            var last = Interlocked.Read(ref _lastClaimStampTicks);
            var next = Math.Max(nowTicks, last + TimeSpan.TicksPerMillisecond);
            if (Interlocked.CompareExchange(ref _lastClaimStampTicks, next, last) == last)
            {
                return new DateTimeOffset(next, TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Publish a claimed upload job's episode, with its staged transcript when its transcription completed. Returns the episode as
    /// stored, or null when it could not be created (EpisodeService then keeps nothing of it). A failed transcript read degrades only the
    /// transcript, never the episode.
    /// </summary>
    private async Task<Episode?> PublishUploadedEpisodeAsync(JobStatusEntity entity, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        try
        {
            // The run staged its transcript for this attempt; the episode's transcript location is written only here, with the episode
            string? transcriptVtt = null;
            if (entity.TranscriptionStatus == TranscriptionStatuses.Completed)
            {
                try
                {
                    transcriptVtt = await StagedTranscripts.ReadCompletedAsync(_blobService, entity);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // An OperationCanceledException not from the caller's token is a storage timeout, a failure like any other
                    _logger.LogError(ex, "Failed to read the staged transcript of job {JobId}; creating its episode with a failed transcript", jobId);
                }
            }

            var transcriptStatus = entity.TranscriptionStatus switch
            {
                TranscriptionStatuses.Completed when transcriptVtt != null => (TranscriptStatus?)TranscriptStatus.Available,
                TranscriptionStatuses.Completed or TranscriptionStatuses.Failed => TranscriptStatus.Failed,
                _ => null
            };

            var episode = new Episode
            {
                Id = entity.EpisodeId!,
                FeedId = entity.FeedId!,
                Title = entity.PublishedTitle!,
                Description = entity.Description,
                Summary = entity.Summary,
                FileName = entity.FileName!,
                FileSize = entity.NormalizedFileSize ?? entity.OriginalFileSize ?? 0,
                Duration = entity.AudioDurationMs.HasValue ? TimeSpan.FromMilliseconds(entity.AudioDurationMs.Value) : TimeSpan.Zero,
                PublishedDate = entity.PublishedDate?.UtcDateTime ?? DateTime.UtcNow,
                Source = Enum.TryParse<UploadSource>(entity.Source, out var source) ? source : UploadSource.CLI,
                UploadedAt = entity.EpisodeUploadedAt!.Value.UtcDateTime,
                TranscriptStatus = transcriptStatus,
                AudioETag = entity.AudioETag
            };

            episode = await _episodeService.AddEpisodeFromEntityAsync(episode, ct, transcriptVtt);
            _feedEventChannel.Publish(entity.FeedId!, "episode-added");
            _logger.LogInformation("Episode {EpisodeId} created for job {JobId}", episode.Id, jobId);

            return episode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to create episode for job {JobId}", jobId);

            return null;
        }
    }

    /// <summary>
    /// Write a claimed upload job's single terminal status from what its join published: Completed with its transcription Failed
    /// (<see cref="TranscriptNotPublishedError"/>) when a completed transcription was not published to <paramref name="published"/>, or
    /// Failed (<see cref="EpisodeNotPublishedError"/>) with the planned episode id removed (a conditional Replace, since a Merge skips
    /// nulls) when nothing was published. Before the Completed write, a deferred title that reached the job while it was claimed (the
    /// title endpoint renames only a completed job's episode) goes to the episode, but only while the episode is still the published
    /// incarnation with <paramref name="expectedTitle"/>, the title this join last gave it, so a rename by the user is kept; the write is
    /// conditional on the read the title came from, so a title arriving after it conflicts and is carried over on the retry. A rename
    /// whose save fails keeps the job claimed (no terminal write), so the recurring recovery carries the title once storage recovers.
    /// Conditional on a fresh read, so it never overwrites a job that is already terminal or on another attempt; returns null then,
    /// when a rename failed, or when the write keeps failing. Each write carries a write token of its own, and any failure of the write
    /// other than the caller's cancellation leaves its outcome unknown, so the job is read again: a terminal row carrying one of this
    /// invocation's tokens means its own write committed although its outcome was lost (an SDK replay of a committed write answers 412,
    /// and a transport failure can follow the commit) and returns that row, so the status is still reported. Otherwise returns the row
    /// as written, which the caller reports from rather than from another read.
    /// </summary>
    private async Task<JobStatusEntity?> TryEndUploadJoinAsync(string jobId, int attemptJoined, Episode? published, string? expectedTitle, CancellationToken ct)
    {
        var issued = new List<string>();
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            var current = await _jobService.GetJobStatusAsync(jobId, ct);
            if (current == null || current.GetAttempt() != attemptJoined)
            {
                return null;
            }

            if (current.GetJobStatus().IsTerminal())
            {
                return current.JoinWriteToken is { } written && issued.Contains(written) ? current : null;
            }

            if (attempt > maxRetries)
            {
                return null;
            }

            var token = NewJoinWriteToken();
            issued.Add(token);
            var completedAt = DateTimeOffset.UtcNow;
            if (published == null)
            {
                current.Status = nameof(JobStatus.Failed);
                current.Error = EpisodeNotPublishedError;
                current.CompletedAt = completedAt;
                current.EpisodeId = null;
                current.JoinWriteToken = token;
                try
                {
                    if (await _jobService.TryReplaceAsync(current, ct))
                    {
                        return current;
                    }

                    _logger.LogDebug("Conflict ending upload join of job {JobId} as failed, attempt {Attempt}", jobId, attempt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Ending upload join of job {JobId} as failed did not answer, attempt {Attempt}; reading the job again", jobId, attempt);
                }

                continue;
            }

            if (expectedTitle != null && current.Title is { } jobTitle && !string.Equals(jobTitle, expectedTitle, StringComparison.Ordinal))
            {
                var renamed = await RenameEpisodeIfTitleIsAsync(published.FeedId, published.Id, expectedTitle, jobTitle, published.UploadedAt, jobId);
                if (renamed == null)
                {
                    return null;
                }

                if (renamed == true)
                {
                    expectedTitle = jobTitle;
                }
            }

            var transcriptNotPublished = current.TranscriptionStatus == TranscriptionStatuses.Completed && published.TranscriptStatus != TranscriptStatus.Available;
            void EndCompleted(JobStatusEntity e)
            {
                e.Status = nameof(JobStatus.Completed);
                e.CompletedAt = completedAt;
                e.JoinWriteToken = token;
                if (transcriptNotPublished)
                {
                    e.TranscriptionStatus = TranscriptionStatuses.Failed;
                    e.TranscriptionError = TranscriptNotPublishedError;
                }
            }

            try
            {
                await _jobService.MergeWithETagAsync(jobId, EndCompleted, current.ETag, ct);
                EndCompleted(current);

                return current;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogDebug("ETag conflict ending upload join of job {JobId}, attempt {Attempt}", jobId, attempt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Ending upload join of job {JobId} did not answer, attempt {Attempt}; reading the job again", jobId, attempt);
            }
        }

        return null;
    }

    /// <summary>
    /// After an upload join's terminal write: report the terminal status from the row that write committed (<paramref name="ended"/>),
    /// so no further job read can lose the report, and delete the job's pending blobs. A stop before either leaves the terminal status
    /// for the poll ingestion points and the pending blobs for the scheduled cleanup.
    /// </summary>
    private async Task FinishUploadJoinAsync(JobStatusEntity claimed, JobStatusEntity ended)
    {
        var jobId = claimed.RowKey;
        PublishTerminalProgress(jobId, ended);

        try
        {
            if (claimed.FeedId != null)
            {
                await _blobService.DeletePendingJobBlobsAsync(claimed.FeedId, jobId);
                _logger.LogDebug("Deleted pending blob for job {JobId}", jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete pending blob for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// End an upload job whose join claimed it and stopped before its terminal write (a server stop, or a join that kept losing that
    /// write), for the recurring recovery, which calls it only for a claim older than its bound (<see cref="JobRetryService.IsClaimedUploadJoin"/>),
    /// since a younger one may be held by a join in another process. A join still running in this process owns the job and is left
    /// alone. The stopped join's publish landed when this process's episodes hold the incarnation its claim recorded (its
    /// <see cref="JobStatusEntity.EpisodeUploadedAt"/>): the job then ends Completed, its transcription Failed when the episode's
    /// transcript is not available. Not finding it proves nothing while a join in another process may still be publishing or this
    /// process's episode cache may predate the publish, so the job ends Failed only when <paramref name="mayEndUnpublished"/> (the
    /// claim is older than the recovery's long bound) and otherwise stays claimed for a later pass. The incarnation is looked up where
    /// the claim put it and, failing that, in every feed, since a move of the published episode keeps its upload time and file name. A
    /// deferred title is carried over from the title the claim recorded (<see cref="JobStatusEntity.PublishedTitle"/>), as the join
    /// would, to the episode at the claim's own feed and id only, since an episode found elsewhere may be a copy; the terminal status
    /// is then reported. Returns whether it ended the job.
    /// </summary>
    internal async Task<bool> EndStoppedUploadJoinAsync(JobStatusEntity job, bool mayEndUnpublished, CancellationToken ct)
    {
        if (job.JoinClaimedAt == null || job.EpisodeUploadedAt is not { } incarnation || !_liveJoins.TryAdd(job.RowKey, 0))
        {
            return false;
        }

        try
        {
            var episode = job.FeedId != null && job.EpisodeId != null ? await _episodeService.GetEpisodeByIdAsync(job.FeedId, job.EpisodeId) : null;
            var atClaim = episode != null && episode.UploadedAt.ToUniversalTime() == incarnation.UtcDateTime;
            var published = atClaim
                ? episode
                : job.FileName != null ? await _episodeService.FindIncarnationAsync(incarnation.UtcDateTime, job.FileName) : null;
            if (published == null && !mayEndUnpublished)
            {
                _logger.LogInformation("Upload join of job {JobId} claimed it and its episode is not found yet; leaving it claimed for a later recovery pass", job.RowKey);

                return false;
            }

            _logger.LogWarning("Upload join of job {JobId} stopped after claiming it; ending it as {Outcome}", job.RowKey, published != null ? "published" : "not published");

            // An episode found elsewhere proves the publish landed, but may be a copy of it rather than the moved original, so the
            // deferred title goes only to the episode at the claim's own feed and id, as the live join and the title endpoint do
            if (await TryEndUploadJoinAsync(job.RowKey, job.GetAttempt(), published, atClaim ? job.PublishedTitle : null, ct) is not { } ended)
            {
                return false;
            }

            await FinishUploadJoinAsync(job, ended);

            return true;
        }
        finally
        {
            _liveJoins.TryRemove(job.RowKey, out _);
        }
    }

    /// <summary>
    /// CAS guard of the join: sets the job's terminal status with the ETag of the read that found both tracks terminal, so exactly one
    /// caller completes a job. Returns the entity the winning
    /// write was based on, or null when another caller completed the job first, the job moved to another attempt (a normalization retry
    /// reset it, so the decision taken on the earlier attempt no longer holds), or the write kept conflicting.
    /// </summary>
    private async Task<JobStatusEntity?> TrySetTerminalStatusAsync(JobStatusEntity entity, string finalStatus, string? error, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        var decidedAttempt = entity.GetAttempt();
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _jobService.MergeWithETagAsync(jobId, e =>
                {
                    e.Status = finalStatus;
                    e.CompletedAt = DateTimeOffset.UtcNow;
                    if (error != null)
                    {
                        e.Error = error;
                    }
                }, entity.ETag, ct);

                return entity;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                // Re-read to check if another caller already completed it, or a retry moved it to a new attempt. Within one attempt the
                // tracks only move toward terminal, so the decision stays valid for a reread of the same attempt
                var reread = await _jobService.GetJobStatusAsync(jobId, ct);
                if (reread == null || reread.GetJobStatus().IsTerminal() || reread.GetAttempt() != decidedAttempt)
                {
                    _logger.LogDebug("Job {JobId} already completed by another caller or moved to another attempt", jobId);

                    return null;
                }

                if (attempt >= maxRetries)
                {
                    _logger.LogWarning("Failed to set terminal status for job {JobId} after {MaxRetries} attempts", jobId, maxRetries);

                    return null;
                }

                entity = reread;
                _logger.LogDebug("ETag conflict setting terminal status for job {JobId}, attempt {Attempt}", jobId, attempt);
            }
        }

        return null;
    }

    /// <summary>Transcription error of a job whose transcription completed but whose join could not publish the transcript.</summary>
    internal const string TranscriptNotPublishedError = "The transcript was not published to the episode";

    /// <summary>Error of an upload job whose normalization succeeded but whose join could not publish the episode.</summary>
    internal const string EpisodeNotPublishedError = "The episode could not be published";

    /// <summary>
    /// Join of a transcript retry job: it ends Completed whether its transcription succeeded or failed, its transcription status telling
    /// the two apart, and its terminal status is written once, after publishing, with the actual outcome, so no reader ever sees a
    /// terminal state that could still change. The join first claims the job with <see cref="JobStatusEntity.JoinClaimedAt"/>, a
    /// conditional write that leaves it non-terminal (a join that finds the claim, or loses the write, returns); a stop after the claim
    /// leaves an active job that the recurring recovery ends (JobRetryService). A publish failure or refusal ends the job with a failed
    /// transcript (<see cref="TranscriptNotPublishedError"/>), so the episode stays retryable. Publishing goes through
    /// EpisodeService.PublishTranscriptIfAsync and is refused unless the episode is still the incarnation the retry was started on, with
    /// a failed transcript, and no other job bound to it is active or completed after the retry was queued, other than the upload that
    /// published that incarnation: a retry never removes or replaces a transcript another job published, and never creates the episode.
    /// Staged blobs are deleted afterwards.
    /// </summary>
    private async Task CompleteTranscriptRetryAsync(JobStatusEntity entity, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        if (entity.JoinClaimedAt != null)
        {
            return;
        }

        // Registered before the claim write, so recovery never sees the claim without the live mark; removed once this join has ended.
        // A join already live for the job owns it.
        if (!_liveJoins.TryAdd(jobId, 0))
        {
            return;
        }

        try
        {
            await RunClaimedTranscriptRetryJoinAsync(entity, ct);
        }
        finally
        {
            _liveJoins.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Whether a join for the job (an upload's or a transcript retry's) is running in this process, or the recovery is ending it. Recovery
    /// leaves such a job alone: the join owns it until it writes the terminal status. The set sees only this process, so a claim it does
    /// not hold is not proof of a stop (an overlapped restart's old process may still run the join); recovery adds claim-age bounds
    /// before it ends a claimed upload join.
    /// </summary>
    public bool IsJoinLive(string jobId) => _liveJoins.ContainsKey(jobId);

    private async Task RunClaimedTranscriptRetryJoinAsync(JobStatusEntity entity, CancellationToken ct)
    {
        var jobId = entity.RowKey;
        try
        {
            await _jobService.MergeWithETagAsync(jobId, e => e.JoinClaimedAt = DateTimeOffset.UtcNow, entity.ETag, ct);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 412)
        {
            // Another join claimed it, or the job changed; whoever changed it completes it
            return;
        }

        var feedId = entity.FeedId!;
        var episodeId = entity.EpisodeId!;
        var transcribed = entity.TranscriptionStatus == TranscriptionStatuses.Completed;
        var published = false;
        if (transcribed)
        {
            try
            {
                published = await PublishRetriedTranscriptAsync(entity, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish the retried transcript of job {JobId} to episode {EpisodeId}", jobId, episodeId);
            }
        }
        else
        {
            _logger.LogInformation("Transcript retry job {JobId} for episode {EpisodeId} failed: {Error}", jobId, episodeId, entity.TranscriptionError);
        }

        if (!await TryEndTranscriptRetryAsync(jobId, transcribed, published, ct))
        {
            return;
        }

        PublishTerminalProgress(jobId, await _jobService.GetJobStatusAsync(jobId, ct));

        try
        {
            await _blobService.DeletePendingJobBlobsAsync(feedId, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete pending blobs of transcript retry job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Write a claimed transcript retry's single terminal status: Completed, with its transcription Completed exactly when this join
    /// published the transcript and Failed otherwise, overriding whatever track status landed after the claim. Conditional on a fresh read, so a cancel landing meanwhile is not overwritten; returns false
    /// when the job is already terminal or the write keeps conflicting.
    /// </summary>
    internal async Task<bool> TryEndTranscriptRetryAsync(string jobId, bool transcribed, bool published, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var current = await _jobService.GetJobStatusAsync(jobId, ct);
            if (current == null || current.GetJobStatus().IsTerminal())
            {
                return false;
            }

            try
            {
                await _jobService.MergeWithETagAsync(jobId, e =>
                {
                    // The track states this join's decision whatever landed on it after the claim, so a late run's Completed never
                    // ends a job that published nothing; a failure already recorded (the run's or the stall's) keeps its error
                    e.Status = nameof(JobStatus.Completed);
                    e.CompletedAt = DateTimeOffset.UtcNow;
                    e.TranscriptionStatus = published ? TranscriptionStatuses.Completed : TranscriptionStatuses.Failed;
                    if (!published && (transcribed || string.IsNullOrEmpty(current.TranscriptionError)))
                    {
                        e.TranscriptionError = TranscriptNotPublishedError;
                    }
                }, current.ETag, ct);

                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412 && attempt < maxRetries)
            {
                _logger.LogDebug("ETag conflict ending transcript retry job {JobId}, attempt {Attempt}", jobId, attempt);
            }
        }

        return false;
    }

    /// <summary>
    /// How far back the join looks for other jobs bound to a retried episode; past the job retention nothing is kept to find.
    /// </summary>
    private static readonly TimeSpan ForeignJobLookback = TimeSpan.FromDays(30);

    /// <summary>
    /// Publish a transcript retry's staged transcript to its episode. Returns whether it published; false when nothing was staged, when
    /// another job bound to the episode is active or completed after this retry was queued (an upload of the same file re-creating the
    /// episode id writes its location itself; the upload that published the retried incarnation does not count), or when EpisodeService
    /// refuses because the episode is gone, re-created or no longer has a failed transcript.
    /// </summary>
    private async Task<bool> PublishRetriedTranscriptAsync(JobStatusEntity job, CancellationToken ct)
    {
        var jobId = job.RowKey;
        var feedId = job.FeedId!;
        var episodeId = job.EpisodeId!;
        var vtt = await _blobService.DownloadPendingTranscriptAsync(feedId, jobId, job.GetAttempt());
        if (vtt == null)
        {
            _logger.LogWarning("Transcript retry job {JobId} completed but staged no transcript; episode {EpisodeId} keeps its failed transcript", jobId, episodeId);

            return false;
        }

        var queuedAt = job.QueuedAt ?? DateTimeOffset.MinValue;
        var feedJobs = await _jobService.GetRecentJobsByFeedAsync(feedId, ForeignJobLookback, ct);
        // Only another job that can write the episode blocks it: one still active (an upload join stays active while it re-creates the
        // episode), or one that completed after this retry was queued. A failed or cancelled job writes nothing, even when a dismissal's
        // cancel moved its CompletedAt; a completed re-creation is also refused by the incarnation check below. The upload that published
        // this very incarnation (its claim recorded the same upload time) is not one: after publishing it never writes the transcript
        // location again, though it stays active, or completes, after its episode became retryable.
        if (feedJobs.Any(j => j.RowKey != jobId && j.EpisodeId == episodeId && !PublishedIncarnation(j, job.EpisodeUploadedAt) &&
            (!j.GetJobStatus().IsTerminal() || (j.GetJobStatus() == JobStatus.Completed && j.CompletedAt > queuedAt))))
        {
            _logger.LogInformation("Not publishing the transcript of retry job {JobId}: another job for episode {EpisodeId} is active or completed after it", jobId, episodeId);

            return false;
        }

        if (job.EpisodeUploadedAt is not { } incarnation ||
            !await _episodeService.PublishTranscriptIfAsync(feedId, episodeId, incarnation.UtcDateTime, () => _blobService.UploadTranscriptAsync(feedId, episodeId, vtt)))
        {
            _logger.LogInformation("Not publishing the transcript of retry job {JobId}: episode {EpisodeId} is gone, re-created or no longer has a failed transcript", jobId, episodeId);

            return false;
        }

        _feedEventChannel.Publish(feedId, "episode-updated");
        _logger.LogInformation("Transcript retry job {JobId} published the transcript of episode {EpisodeId}", jobId, episodeId);

        return true;
    }

    /// <summary>Whether <paramref name="job"/> is the upload whose join published the episode incarnation uploaded at <paramref name="incarnation"/>.</summary>
    private static bool PublishedIncarnation(JobStatusEntity job, DateTimeOffset? incarnation) =>
        !JobKinds.IsTranscriptRetry(job.Kind) && job.JoinClaimedAt != null && incarnation is { } uploadedAt && job.EpisodeUploadedAt == uploadedAt;

    /// <summary>
    /// Replaces an untitled job's placeholder title with its deferred (AI or heuristic) title. When the join has already
    /// completed the job, the title also goes to the episode the job published, as long as that episode still carries the
    /// placeholder; when the join completes the job later, it reads the title itself. If the episode does not exist yet,
    /// the join's own re-read after publishing carries the title over. Returns false when the job could not be updated.
    /// </summary>
    public async Task<bool> ApplyDeferredTitleAsync(string jobId, string title, string placeholderTitle, CancellationToken ct)
    {
        var updated = await _jobService.UpdateJobStatusAsync(jobId, e => e.Title = title, ct);
        if (updated == null)
        {
            return false;
        }

        if (updated.GetJobStatus() == JobStatus.Completed && updated.FeedId != null && updated.EpisodeId != null)
        {
            await RenameEpisodeIfTitleIsAsync(updated.FeedId, updated.EpisodeId, placeholderTitle, title, updated.EpisodeUploadedAt?.UtcDateTime, jobId);
        }

        return true;
    }

    /// <summary>
    /// Renames a job's episode from the title it was published under to a deferred title that arrived later, unless the
    /// episode is gone, is another incarnation than the one uploaded at <paramref name="expectedUploadedAt"/> (when given) or has been
    /// renamed since. Returns true when it renamed, false when the episode was left alone, and null when the rename failed (the episode
    /// keeps its title); a failure is logged, not thrown, so the caller's remaining steps run.
    /// </summary>
    private async Task<bool?> RenameEpisodeIfTitleIsAsync(string feedId, string episodeId, string expectedTitle, string newTitle, DateTime? expectedUploadedAt, string jobId)
    {
        if (string.Equals(expectedTitle, newTitle, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (await _episodeService.RenameEpisodeIfTitleIsAsync(feedId, episodeId, expectedTitle, newTitle, expectedUploadedAt) == null)
            {
                return false;
            }

            _feedEventChannel.Publish(feedId, "episode-updated");
            _logger.LogInformation("Episode {EpisodeId} of job {JobId} took its deferred title", episodeId, jobId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to give episode {EpisodeId} of job {JobId} its deferred title", episodeId, jobId);

            return null;
        }
    }

    private void PublishTerminalProgress(string jobId, JobStatusEntity? fresh)
    {
        if (fresh != null)
        {
            var response = JobStatusResponse.FromEntity(fresh);
            _progressChannel.Publish(jobId, response);
            _pushNotificationService.TryNotifyJobTerminal(response);
        }
    }
}
