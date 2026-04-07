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

        _logger.LogInformation("Both tracks terminal for job {JobId}, creating episode", jobId);

        // CAS guard: attempt to set Status=Completed (or Failed) with ETag
        var normFailed = !string.IsNullOrEmpty(entity.NormalizationError);
        var finalStatus = normFailed ? nameof(JobStatus.Failed) : nameof(JobStatus.Completed);

        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _jobService.MergeWithETagAsync(jobId, e =>
                {
                    e.Status = finalStatus;
                    e.CompletedAt = DateTimeOffset.UtcNow;
                    if (normFailed)
                    {
                        e.Error = entity.NormalizationError;
                    }
                }, entity.ETag, ct);

                break;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                // Re-read to check if another caller already completed it
                entity = await _jobService.GetJobStatusAsync(jobId, ct);
                if (entity == null || entity.GetJobStatus() is JobStatus.Completed or JobStatus.Failed)
                {
                    _logger.LogDebug("Job {JobId} already completed by another caller", jobId);

                    return;
                }

                if (attempt >= maxRetries)
                {
                    _logger.LogWarning("Failed to set terminal status for job {JobId} after {MaxRetries} attempts", jobId, maxRetries);

                    return;
                }

                _logger.LogDebug("ETag conflict setting terminal status for job {JobId}, attempt {Attempt}", jobId, attempt);
            }
        }

        // Normalization failed: no episode created, job is Failed
        if (normFailed)
        {
            _logger.LogWarning("Normalization failed for job {JobId}, no episode created", jobId);
            await PublishTerminalProgress(jobId, ct);

            return;
        }

        // Create episode from entity metadata
        try
        {
            var latestTitle = entity.Title ?? entity.FileName ?? "Untitled";
            var source = Enum.TryParse<UploadSource>(entity.Source, out var src) ? src : UploadSource.CLI;
            var publishedDate = entity.PublishedDate?.UtcDateTime ?? DateTime.UtcNow;
            var fileSize = entity.NormalizedFileSize ?? entity.OriginalFileSize ?? 0;
            var duration = entity.AudioDurationMs.HasValue
                ? TimeSpan.FromMilliseconds(entity.AudioDurationMs.Value)
                : TimeSpan.Zero;

            var transcriptStatus = entity.TranscriptionStatus switch
            {
                TranscriptionStatuses.Completed => (TranscriptStatus?)TranscriptStatus.Available,
                TranscriptionStatuses.Failed => TranscriptStatus.Failed,
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
                TranscriptStatus = transcriptStatus
            };

            await _episodeService.AddEpisodeFromEntityAsync(episode, ct);

            // Set EpisodeId on entity (ETag.All = unconditional, CAS already passed)
            await _jobService.MergeWithETagAsync(jobId, e =>
            {
                e.EpisodeId = episode.Id;
            }, Azure.ETag.All, ct);

            _feedEventChannel.Publish(entity.FeedId!, "episode-added");
            _logger.LogInformation("Episode {EpisodeId} created for job {JobId}", episode.Id, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create episode for job {JobId}", jobId);
        }

        // Publish terminal progress and push notification
        await PublishTerminalProgress(jobId, ct);

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

    private async Task PublishTerminalProgress(string jobId, CancellationToken ct)
    {
        // Re-read for accurate state
        var fresh = await _jobService.GetJobStatusAsync(jobId, ct);
        if (fresh != null)
        {
            var response = JobStatusResponse.FromEntity(fresh);
            _progressChannel.Publish(jobId, response);
            _pushNotificationService.TryNotifyJobTerminal(response);
        }
    }
}
