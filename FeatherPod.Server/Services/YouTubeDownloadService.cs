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
    private readonly FFmpegBinaryManager _ffmpegBinaryManager;
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
        FFmpegBinaryManager ffmpegBinaryManager,
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
        _ffmpegBinaryManager = ffmpegBinaryManager;
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
        // Step 1: Preparing - ensure binaries and fetch metadata
        await UpdateProgressAsync(job, NormalizationStage.Preparing, 0, "Preparing...", stoppingToken);

        var available = await _binaryManager.EnsureAvailableAsync(stoppingToken);
        if (!available)
        {
            await MarkJobFailedAsync(job, "Failed to download yt-dlp");

            return;
        }

        var ffmpegAvailable = await _ffmpegBinaryManager.EnsureFFmpegAvailableAsync(stoppingToken);
        if (!ffmpegAvailable)
        {
            await MarkJobFailedAsync(job, "Failed to download ffmpeg");

            return;
        }

        var ffmpegDir = GetLocalFfmpegDir();

        // Create output directory early -- also used as cookie temp dir
        var outputDir = Path.Combine(Path.GetTempPath(), "FeatherPod", job.JobId);

        try
        {
            string? cookiePath = null;
            try
            {
                cookiePath = await _cookieService.GetCookieFilePathAsync(outputDir, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get cookie file for job {JobId}, proceeding without cookies", job.JobId);
            }

            // Fetch metadata via yt-dlp (title, description, channel, duration, upload date)
            await UpdateProgressAsync(job, NormalizationStage.Preparing, 0, "Fetching video info...", stoppingToken);

            var canonicalUrl = YtDlpService.GetCanonicalUrl(job.VideoId);
            var (metadata, metaError) = await _ytDlpService.GetMetadataAsync(canonicalUrl, cookiePath, stoppingToken);
            if (metadata == null)
            {
                if (metaError != null && YtDlpService.IsBotDetectionError(metaError))
                {
                    await MarkJobFailedAsync(job, YtDlpService.BotDetectionErrorMessage);

                    return;
                }

                await MarkJobFailedAsync(job, metaError ?? "Video is unavailable");

                return;
            }

            // Extract metadata into local variables for use in episode creation
            var title = metadata.Title ?? job.Title;
            var channel = metadata.Channel;
            var description = !string.IsNullOrEmpty(channel)
                ? $"By {channel}\n\n{metadata.Description}"
                : metadata.Description;
            var uploadDate = metadata.GetUploadDateTime();

            // Update queue UI with authoritative yt-dlp title if it differs from oEmbed title
            if (title != job.Title)
            {
                await _jobService.UpdateJobStatusAsync(job.JobId, e =>
                {
                    if (e.GetJobStatus() is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
                    {
                        e.Title = title;
                    }
                }, stoppingToken);
            }

            if (await IsJobCancelledAsync(job.JobId, stoppingToken))
            {
                return;
            }

            // Step 2: Downloading
            await UpdateProgressAsync(job, NormalizationStage.Downloading, 5, "Downloading...", stoppingToken);

            string? outputPath = null;
            string? lastStderr = null;
            var retried = false;

            (outputPath, lastStderr) = await AttemptDownloadAsync(job, outputDir, cookiePath, ffmpegDir, stoppingToken);

            if (outputPath == null && lastStderr != null && YtDlpService.IsBotDetectionError(lastStderr))
            {
                await MarkJobFailedAsync(job, YtDlpService.BotDetectionErrorMessage);

                return;
            }

            if (outputPath == null && lastStderr != null && YtDlpService.IsFormatUnavailableError(lastStderr))
            {
                _logger.LogError("Format unavailable for job {JobId} ({Format}). Ensure deno is installed on the server for full format support.", job.JobId, job.Format);
                await MarkJobFailedAsync(job, $"The requested {job.Format.ToString().ToLowerInvariant()} format is not available for this video");

                return;
            }

            if (outputPath == null && lastStderr != null && YtDlpService.IsExtractorError(lastStderr))
            {
                retried = true;
                _logger.LogInformation("Extractor error for job {JobId}, attempting yt-dlp update...", job.JobId);

                var updated = await _binaryManager.TryUpdateAsync(stoppingToken);
                if (updated)
                {
                    _logger.LogInformation("yt-dlp updated, retrying download for job {JobId}", job.JobId);
                    (outputPath, lastStderr) = await AttemptDownloadAsync(job, outputDir, cookiePath, ffmpegDir, stoppingToken);
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

            await _episodeService.AddEpisodeAsync(
                job.FeedId,
                outputPath,
                title: title,
                description: description,
                publishedDate: uploadDate,
                episodeId: job.EpisodeId,
                source: UploadSource.Browser,
                mediaSource: MediaSource.YouTube,
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
        string? ffmpegDir,
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
            ffmpegDir: ffmpegDir,
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

    private static string? GetLocalFfmpegDir()
    {
        var dir = FFmpegBinaryManager.GetBinaryDirectory();
        var ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        return File.Exists(Path.Combine(dir, ffmpegName)) ? dir : null;
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
