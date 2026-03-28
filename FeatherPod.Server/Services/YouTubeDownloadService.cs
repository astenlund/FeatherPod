using System.Threading.Channels;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

/// <summary>
/// Background service that processes YouTube download jobs from an in-memory channel.
/// Orchestrates: yt-dlp download -> blob upload -> episode creation -> progress reporting.
/// </summary>
public class YouTubeDownloadService : BackgroundService
{
    private readonly Channel<YouTubeDownloadJob> _channel;
    private readonly YtDlpBinaryManager _binaryManager;
    private readonly YtDlpService _ytDlpService;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly EpisodeService _episodeService;
    private readonly PushNotificationService _pushNotificationService;
    private readonly YouTubeCookieService _cookieService;
    private readonly int _throttleMs;
    private readonly ILogger<YouTubeDownloadService> _logger;

    public YouTubeDownloadService(
        Channel<YouTubeDownloadJob> channel,
        YtDlpBinaryManager binaryManager,
        YtDlpService ytDlpService,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        IFeedEventChannel feedEventChannel,
        EpisodeService episodeService,
        PushNotificationService pushNotificationService,
        YouTubeCookieService cookieService,
        IConfiguration configuration,
        ILogger<YouTubeDownloadService> logger)
    {
        _channel = channel;
        _binaryManager = binaryManager;
        _ytDlpService = ytDlpService;
        _jobService = jobService;
        _progressChannel = progressChannel;
        _feedEventChannel = feedEventChannel;
        _episodeService = episodeService;
        _pushNotificationService = pushNotificationService;
        _cookieService = cookieService;
        _throttleMs = configuration.GetValue("PushPage:ProgressIntervalMs", 250);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("YouTubeDownloadService started");

        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await MarkJobFailedAsync(job, "Server restarting");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing YouTube job {JobId}", job.JobId);
                    await MarkJobFailedAsync(job, "An unexpected error occurred");
                }
            }
        }
        finally
        {
            while (_channel.Reader.TryRead(out var remainingJob))
            {
                await MarkJobFailedAsync(remainingJob, "Server restarting");
            }

            _logger.LogInformation("YouTubeDownloadService stopped");
        }
    }

    private async Task ProcessJobAsync(YouTubeDownloadJob job, CancellationToken stoppingToken)
    {
        // Step 1: Preparing - ensure yt-dlp is available
        await UpdateProgressAsync(job, NormalizationStage.Preparing, 0, "Preparing...", stoppingToken);

        var available = await _binaryManager.EnsureAvailableAsync(stoppingToken);
        if (!available)
        {
            await MarkJobFailedAsync(job, "Failed to download yt-dlp");

            return;
        }

        if (await IsJobCancelledAsync(job.JobId, stoppingToken))
        {
            return;
        }

        // Step 2: Downloading
        await UpdateProgressAsync(job, NormalizationStage.Downloading, 5, "Downloading...", stoppingToken);

        var outputDir = Path.Combine(Path.GetTempPath(), "FeatherPod", job.JobId);
        string? outputPath = null;
        string? lastStderr = null;
        var retried = false;

        // Get cookie file for yt-dlp if available
        string? cookiePath = null;
        try
        {
            cookiePath = await _cookieService.GetCookieFilePathAsync(outputDir, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get cookie file for job {JobId}, proceeding without cookies", job.JobId);
        }

        try
        {
            (outputPath, lastStderr) = await AttemptDownloadAsync(job, outputDir, cookiePath, stoppingToken);

            // Check bot detection BEFORE extractor error - auth errors should not trigger yt-dlp update
            if (outputPath == null && lastStderr != null && YtDlpService.IsBotDetectionError(lastStderr))
            {
                await MarkJobFailedAsync(job, YtDlpService.BotDetectionErrorMessage);

                return;
            }

            // Update-then-retry only on extractor errors (YouTube-side changes)
            if (outputPath == null && lastStderr != null && YtDlpService.IsExtractorError(lastStderr))
            {
                retried = true;
                _logger.LogInformation("Extractor error for job {JobId}, attempting yt-dlp update...", job.JobId);

                var updated = await _binaryManager.TryUpdateAsync(stoppingToken);
                if (updated)
                {
                    _logger.LogInformation("yt-dlp updated, retrying download for job {JobId}", job.JobId);
                    (outputPath, lastStderr) = await AttemptDownloadAsync(job, outputDir, cookiePath, stoppingToken);
                }
            }

            if (outputPath == null)
            {
                await MarkJobFailedAsync(job, retried
                    ? "Download failed. YouTube may have changed their site."
                    : "Download failed");

                return;
            }

            if (await IsJobCancelledAsync(job.JobId, stoppingToken))
            {
                return;
            }

            // Step 3: Finishing - upload and create episode
            await UpdateProgressAsync(job, NormalizationStage.Finishing, 90, "Uploading to feed...", stoppingToken);

            var description = !string.IsNullOrEmpty(job.Channel)
                ? $"By {job.Channel}\n\n{job.Description}"
                : job.Description;

            await _episodeService.AddEpisodeAsync(
                job.FeedId,
                outputPath,
                title: job.Title,
                description: description,
                publishedDate: job.UploadDate,
                episodeId: job.EpisodeId,
                source: UploadSource.Browser,
                cancellationToken: stoppingToken);

            // Step 4: Completed
            await UpdateProgressAsync(job, NormalizationStage.Completed, 100, "Done", stoppingToken, isTerminal: true);
            _feedEventChannel.Publish(job.FeedId, "episode-added");
        }
        finally
        {
            CleanupTempDirectory(outputDir);
        }
    }

    private async Task<(string? OutputPath, string? Stderr)> AttemptDownloadAsync(
        YouTubeDownloadJob job,
        string outputDir,
        string? cookiePath,
        CancellationToken stoppingToken)
    {
        var lastUpdate = DateTime.MinValue;

        var result = await _ytDlpService.DownloadAsync(
            YtDlpService.GetCanonicalUrl(job.VideoId),
            job.VideoId,
            job.Format,
            outputDir,
            progressCallback: percent =>
            {
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < _throttleMs)
                {
                    return;
                }

                lastUpdate = DateTime.UtcNow;

                // Map yt-dlp 0-100% to job 5-90%
                var jobPercent = 5 + (int)(percent * 0.85);
                var message = $"Downloading... {percent:F0}%";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await UpdateProgressAsync(job, NormalizationStage.Downloading, jobPercent, message, stoppingToken);
                    }
                    catch
                    {
                        // Don't fail the download if progress update fails
                    }
                });
            },
            cookieFilePath: cookiePath,
            cancellationToken: stoppingToken);

        return result;
    }

    private async Task UpdateProgressAsync(
        YouTubeDownloadJob job,
        NormalizationStage stage,
        int progressPercent,
        string message,
        CancellationToken cancellationToken,
        bool isTerminal = false)
    {
        try
        {
            var entity = await _jobService.UpdateJobStatusAsync(job.JobId, e =>
            {
                // Don't overwrite terminal states
                if (e.GetJobStatus() is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                {
                    return;
                }

                e.Stage = stage.ToString();
                e.ProgressPercent = progressPercent;
                e.ProgressMessage = message;

                if (stage == NormalizationStage.Completed)
                {
                    e.Status = nameof(JobStatus.Completed);
                    e.EpisodeId = job.EpisodeId;
                    e.CompletedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    e.Status = nameof(JobStatus.Processing);
                }
            }, cancellationToken);

            if (entity != null)
            {
                var response = JobStatusResponse.FromEntity(entity);
                _progressChannel.Publish(job.JobId, response);

                if (isTerminal)
                {
                    _pushNotificationService.TryNotifyJobTerminal(response);
                }
            }
        }
        catch (Exception ex) when (!isTerminal)
        {
            _logger.LogDebug(ex, "Failed to update progress for job {JobId}", job.JobId);
        }
    }

    private async Task MarkJobFailedAsync(YouTubeDownloadJob job, string error)
    {
        try
        {
            var entity = await _jobService.UpdateJobStatusAsync(job.JobId, e =>
            {
                // Don't overwrite terminal states (e.g., already Cancelled or Completed)
                if (e.GetJobStatus() is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                {
                    return;
                }

                e.Status = nameof(JobStatus.Failed);
                e.Stage = nameof(NormalizationStage.Failed);
                e.Error = error;
                e.CompletedAt = DateTimeOffset.UtcNow;
            });

            if (entity != null)
            {
                var response = JobStatusResponse.FromEntity(entity);
                _progressChannel.Publish(job.JobId, response);
                _pushNotificationService.TryNotifyJobTerminal(response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark job {JobId} as failed", job.JobId);
        }
    }

    private async Task<bool> IsJobCancelledAsync(string jobId, CancellationToken cancellationToken)
    {
        var entity = await _jobService.GetJobStatusAsync(jobId, cancellationToken);

        return entity?.GetJobStatus() == JobStatus.Cancelled;
    }

    private void CleanupTempDirectory(string outputDir)
    {
        try
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", outputDir);
        }
    }
}
