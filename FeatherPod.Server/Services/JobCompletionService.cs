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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _liveTranscriptRetryJoins = new(StringComparer.Ordinal);

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

        _logger.LogInformation("Both tracks terminal for job {JobId}, creating episode", jobId);

        var normFailed = !string.IsNullOrEmpty(entity.NormalizationError);
        var finalStatus = normFailed ? nameof(JobStatus.Failed) : nameof(JobStatus.Completed);
        if (await TrySetTerminalStatusAsync(entity, finalStatus, normFailed ? entity.NormalizationError : null, ct) is not { } claimed)
        {
            return;
        }

        entity = claimed;

        // Normalization failed: no episode created, job is Failed
        if (normFailed)
        {
            _logger.LogWarning("Normalization failed for job {JobId}, no episode created", jobId);
            PublishTerminalProgress(jobId, await _jobService.GetJobStatusAsync(jobId, ct));

            return;
        }

        // Create episode from entity metadata
        Episode? published = null;
        try
        {
            var latestTitle = entity.Title ?? entity.FileName ?? "Untitled";
            var source = Enum.TryParse<UploadSource>(entity.Source, out var src) ? src : UploadSource.CLI;
            var publishedDate = entity.PublishedDate?.UtcDateTime ?? DateTime.UtcNow;
            var fileSize = entity.NormalizedFileSize ?? entity.OriginalFileSize ?? 0;
            var duration = entity.AudioDurationMs.HasValue
                ? TimeSpan.FromMilliseconds(entity.AudioDurationMs.Value)
                : TimeSpan.Zero;

            // The run staged its transcript for this attempt; the episode's transcript location is written only here, with the episode.
            // A failed read degrades only the transcript (a Completed run without a transcript publishes a failed one), never the episode.
            string? transcriptVtt = null;
            if (entity.TranscriptionStatus == TranscriptionStatuses.Completed)
            {
                try
                {
                    transcriptVtt = await StagedTranscripts.ReadCompletedAsync(_blobService, entity);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
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
                Id = entity.EpisodeId ?? Episode.GenerateId(entity.FeedId!, entity.FileName!, fileSize),
                FeedId = entity.FeedId!,
                Title = latestTitle,
                Description = entity.Description,
                Summary = entity.Summary,
                FileName = entity.FileName!,
                FileSize = fileSize,
                Duration = duration,
                PublishedDate = publishedDate,
                Source = source,
                UploadedAt = DateTime.UtcNow,
                TranscriptStatus = transcriptStatus,
                AudioETag = entity.AudioETag
            };

            episode = await _episodeService.AddEpisodeFromEntityAsync(episode, ct, transcriptVtt);

            // Set EpisodeId on entity (ETag.All = unconditional, CAS already passed). A completed transcription the join could not
            // publish (its staged transcript unreadable, or its write failed) is reported as failed with the episode, as the transcript
            // retry join reports it, so every reader offers Retry transcript
            var transcriptNotPublished = entity.TranscriptionStatus == TranscriptionStatuses.Completed && episode.TranscriptStatus == TranscriptStatus.Failed;
            await _jobService.MergeWithETagAsync(jobId, e =>
            {
                e.EpisodeId = episode.Id;
                if (transcriptNotPublished)
                {
                    e.TranscriptionStatus = TranscriptionStatuses.Failed;
                    e.TranscriptionError = TranscriptNotPublishedError;
                }
            }, Azure.ETag.All, ct);

            _feedEventChannel.Publish(entity.FeedId!, "episode-added");
            _logger.LogInformation("Episode {EpisodeId} created for job {JobId}", episode.Id, jobId);
            published = episode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create episode for job {JobId}", jobId);
        }

        // A deferred title written after the terminal status above found no episode to rename yet, so carry it over now
        var fresh = await _jobService.GetJobStatusAsync(jobId, ct);
        if (published != null && fresh?.Title is { } currentTitle && !string.Equals(currentTitle, published.Title, StringComparison.Ordinal))
        {
            await RenameEpisodeIfTitleIsAsync(published.FeedId, published.Id, published.Title, currentTitle, jobId);
        }

        // Publish terminal progress and push notification
        PublishTerminalProgress(jobId, fresh);

        // Clean up pending blob
        try
        {
            if (entity.FeedId != null)
            {
                await _blobService.DeletePendingJobBlobsAsync(entity.FeedId, jobId);
                _logger.LogDebug("Deleted pending blob for job {JobId}", jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete pending blob for job {JobId}", jobId);
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

    /// <summary>
    /// Join of a transcript retry job: it ends Completed whether its transcription succeeded or failed, its transcription status telling
    /// the two apart, and its terminal status is written once, after publishing, with the actual outcome, so no reader ever sees a
    /// terminal state that could still change. The join first claims the job with <see cref="JobStatusEntity.JoinClaimedAt"/>, a
    /// conditional write that leaves it non-terminal (a join that finds the claim, or loses the write, returns); a stop after the claim
    /// leaves an active job that the recurring recovery ends (JobRetryService). A publish failure or refusal ends the job with a failed
    /// transcript (<see cref="TranscriptNotPublishedError"/>), so the episode stays retryable. Publishing goes through
    /// EpisodeService.PublishTranscriptIfAsync and is refused unless the episode is still the incarnation the retry was started on, with
    /// a failed transcript, and no other job bound to it is active or completed after the retry was queued: a retry never removes or replaces
    /// a transcript another job published, and never creates the episode. Staged blobs are deleted afterwards.
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
        if (!_liveTranscriptRetryJoins.TryAdd(jobId, 0))
        {
            return;
        }

        try
        {
            await RunClaimedTranscriptRetryJoinAsync(entity, ct);
        }
        finally
        {
            _liveTranscriptRetryJoins.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Whether a transcript retry join for the job is running in this process. Recovery leaves such a job alone: the join owns it until
    /// it writes the terminal status. After a restart nothing is live, so a claimed job is known to be a stopped join. Relies on the
    /// single-instance App Service that EpisodeService's in-memory cache already assumes.
    /// </summary>
    public bool IsTranscriptRetryJoinLive(string jobId) => _liveTranscriptRetryJoins.ContainsKey(jobId);

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
    /// episode id writes its location itself), or when EpisodeService refuses because the episode is gone, re-created or no longer has
    /// a failed transcript.
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
        // Only another job that can write the episode blocks it: one still active, or one that completed after this retry was queued
        // (between an upload join's terminal claim and its re-creation of the episode). A failed or cancelled job writes nothing, even
        // when a dismissal's cancel moved its CompletedAt; a completed re-creation is also refused by the incarnation check below.
        if (feedJobs.Any(j => j.RowKey != jobId && j.EpisodeId == episodeId &&
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
            await RenameEpisodeIfTitleIsAsync(updated.FeedId, updated.EpisodeId, placeholderTitle, title, jobId);
        }

        return true;
    }

    /// <summary>
    /// Renames a job's episode from the title it was published under to a deferred title that arrived later, unless the
    /// episode is gone or has been renamed since. A failure is logged, not thrown, so the caller's remaining steps run.
    /// </summary>
    private async Task RenameEpisodeIfTitleIsAsync(string feedId, string episodeId, string expectedTitle, string newTitle, string jobId)
    {
        if (string.Equals(expectedTitle, newTitle, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (await _episodeService.RenameEpisodeIfTitleIsAsync(feedId, episodeId, expectedTitle, newTitle) != null)
            {
                _feedEventChannel.Publish(feedId, "episode-updated");
                _logger.LogInformation("Episode {EpisodeId} of job {JobId} took its deferred title", episodeId, jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to give episode {EpisodeId} of job {JobId} its deferred title", episodeId, jobId);
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
