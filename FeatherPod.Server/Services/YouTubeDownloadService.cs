using System.Threading.Channels;
using Azure.Data.Tables;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<YouTubeDownloadService> _logger;
    private readonly TableClient _tableClient;

    public YouTubeDownloadService(
        Channel<YouTubeDownloadJob> channel,
        YtDlpBinaryManager binaryManager,
        YtDlpService ytDlpService,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        IFeedEventChannel feedEventChannel,
        EpisodeService episodeService,
        PushNotificationService pushNotificationService,
        IConfiguration configuration,
        ILogger<YouTubeDownloadService> logger,
        TableServiceClient tableServiceClient)
    {
        _channel = channel;
        _binaryManager = binaryManager;
        _ytDlpService = ytDlpService;
        _jobService = jobService;
        _progressChannel = progressChannel;
        _feedEventChannel = feedEventChannel;
        _episodeService = episodeService;
        _pushNotificationService = pushNotificationService;
        _configuration = configuration;
        _logger = logger;
        _tableClient = tableServiceClient.GetTableClient("normalizationjobs");
    }

    public ChannelWriter<YouTubeDownloadJob> Writer => _channel.Writer;

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
            // Drain remaining queued jobs on shutdown
            while (_channel.Reader.TryRead(out var remainingJob))
            {
                await MarkJobFailedAsync(remainingJob, "Server restarting");
            }

            _logger.LogInformation("YouTubeDownloadService stopped");
        }
    }

    private async Task ProcessJobAsync(YouTubeDownloadJob job, CancellationToken stoppingToken)
    {
        var throttleMs = _configuration.GetValue("PushPage:ProgressIntervalMs", 250);

        // Step 1: Preparing - ensure yt-dlp is available
        await UpdateProgressAsync(job, NormalizationStage.Preparing, 0, "Preparing...", stoppingToken);

        var available = await _binaryManager.EnsureAvailableAsync(stoppingToken);
        if (!available)
        {
            await MarkJobFailedAsync(job, "Failed to download yt-dlp");

            return;
        }

        // Check cancellation before download
        if (await IsJobCancelledAsync(job.JobId, stoppingToken))
        {
            return;
        }

        // Step 2: Downloading
        await UpdateProgressAsync(job, NormalizationStage.Downloading, 5, "Downloading...", stoppingToken);

        var outputDir = Path.Combine(Path.GetTempPath(), "FeatherPod", job.JobId);
        string? outputPath = null;
        var retried = false;

        try
        {
            outputPath = await AttemptDownloadAsync(job, outputDir, throttleMs, stoppingToken);

            // Update-then-retry on extractor error
            if (outputPath == null && !retried)
            {
                retried = true;
                _logger.LogInformation("Download failed for job {JobId}, attempting yt-dlp update...", job.JobId);

                var updated = await _binaryManager.TryUpdateAsync(stoppingToken);
                if (updated)
                {
                    _logger.LogInformation("yt-dlp updated, retrying download for job {JobId}", job.JobId);
                    outputPath = await AttemptDownloadAsync(job, outputDir, throttleMs, stoppingToken);
                }
            }

            if (outputPath == null)
            {
                await MarkJobFailedAsync(job, retried
                    ? "Download failed. YouTube may have changed their site."
                    : "Download failed");

                return;
            }

            // Check cancellation before upload
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
                source: UploadSource.YouTube,
                cancellationToken: stoppingToken);

            // Step 4: Completed
            await UpdateProgressAsync(job, NormalizationStage.Completed, 100, "Done", stoppingToken, isTerminal: true);
            _feedEventChannel.Publish(job.FeedId, "episode-added");
        }
        finally
        {
            // Clean up temp directory
            CleanupTempDirectory(outputDir);
        }
    }

    private async Task<string?> AttemptDownloadAsync(
        YouTubeDownloadJob job,
        string outputDir,
        int throttleMs,
        CancellationToken stoppingToken)
    {
        var lastUpdate = DateTime.MinValue;

        var outputPath = await _ytDlpService.DownloadAsync(
            job.Url,
            job.VideoId,
            job.Format,
            outputDir,
            progressCallback: percent =>
            {
                if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds < throttleMs)
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
            cancellationToken: stoppingToken);

        return outputPath;
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
            // Update Table Storage
            var entity = await _tableClient.GetEntityAsync<JobStatusEntity>("job", job.JobId, cancellationToken: cancellationToken);
            var jobEntity = entity.Value;

            jobEntity.Stage = stage.ToString();
            jobEntity.ProgressPercent = progressPercent;
            jobEntity.ProgressMessage = message;

            if (stage == NormalizationStage.Completed)
            {
                jobEntity.Status = nameof(JobStatus.Completed);
                jobEntity.EpisodeId = job.EpisodeId;
                jobEntity.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                jobEntity.Status = nameof(JobStatus.Processing);
            }

            await _tableClient.UpdateEntityAsync(jobEntity, jobEntity.ETag, Azure.Data.Tables.TableUpdateMode.Replace, cancellationToken);

            // Publish to in-memory channel for SSE/SignalR clients
            var response = JobStatusResponse.FromEntity(jobEntity);
            _progressChannel.Publish(job.JobId, response);

            // Notify on terminal status
            if (isTerminal)
            {
                _pushNotificationService.TryNotifyJobTerminal(response);
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
            var entity = await _tableClient.GetEntityAsync<JobStatusEntity>("job", job.JobId);
            var jobEntity = entity.Value;

            jobEntity.Status = nameof(JobStatus.Failed);
            jobEntity.Stage = nameof(NormalizationStage.Failed);
            jobEntity.Error = error;
            jobEntity.CompletedAt = DateTimeOffset.UtcNow;

            await _tableClient.UpdateEntityAsync(jobEntity, jobEntity.ETag, Azure.Data.Tables.TableUpdateMode.Replace);

            var response = JobStatusResponse.FromEntity(jobEntity);
            _progressChannel.Publish(job.JobId, response);
            _pushNotificationService.TryNotifyJobTerminal(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark job {JobId} as failed", job.JobId);
        }
    }

    private async Task<bool> IsJobCancelledAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await _tableClient.GetEntityAsync<JobStatusEntity>("job", jobId, cancellationToken: cancellationToken);

            return string.Equals(entity.Value.Status, nameof(JobStatus.Cancelled), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
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
