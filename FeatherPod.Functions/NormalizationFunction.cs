using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FFMpegCore;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using static FeatherPod.Shared.Models.NormalizationStage;

namespace FeatherPod.Functions;

/// <summary>
/// Azure Function that processes audio normalization jobs from the queue.
/// Jobs are split into two phases (Analyze and Normalize) to stay within
/// Azure Functions Consumption plan timeout limits.
/// </summary>
public class NormalizationFunction
{
    private readonly BlobServiceClient _blobClient;
    private readonly TableServiceClient _tableClient;
    private readonly QueueServiceClient _queueClient;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelemetryClient _telemetryClient;
    private readonly FunctionSettings _settings;
    private readonly ILogger<NormalizationFunction> _logger;

    private const string TableName = "normalizationjobs";
    private const string QueueName = "normalization-jobs";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressUpdateThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public NormalizationFunction(
        BlobServiceClient blobClient,
        TableServiceClient tableClient,
        QueueServiceClient queueClient,
        IAudioNormalizationService normalizationService,
        IHttpClientFactory httpClientFactory,
        TelemetryClient telemetryClient,
        IOptions<FunctionSettings> settings,
        ILogger<NormalizationFunction> logger)
    {
        _blobClient = blobClient;
        _tableClient = tableClient;
        _queueClient = queueClient;
        _normalizationService = normalizationService;
        _httpClientFactory = httpClientFactory;
        _telemetryClient = telemetryClient;
        _settings = settings.Value;
        _logger = logger;
    }

    [Function("ProcessNormalizationJob")]
    public async Task ProcessNormalizationJob([QueueTrigger(QueueName, Connection = "AzureWebJobsStorage")] string? message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Queue trigger received message: {MessageLength} chars", message?.Length ?? 0);

        NormalizationJob job;
        try
        {
            job = JsonSerializer.Deserialize<NormalizationJob>(message!, JsonOptions) ?? throw new InvalidOperationException("Failed to deserialize normalization job");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message. Raw message: {Message}", message);
            throw;
        }

        _logger.LogInformation("Processing normalization job {JobId} phase {Phase} for {FeedId}/{FileName}",
            job.JobId, job.Phase, job.FeedId, job.FileName);
        _logger.LogDebug("Environment: HOME={Home}, TEMP={Temp}, FFmpegDir={FfmpegDir}",
            Environment.GetEnvironmentVariable("HOME"),
            Path.GetTempPath(),
            FFmpegBinaryManager.GetBinaryDirectory());
        _telemetryClient.Flush();

        var tableClient = _tableClient.GetTableClient(TableName);
        await tableClient.CreateIfNotExistsAsync(cancellationToken);

        await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Processing, cancellationToken: cancellationToken);

        if (job.Phase == NormalizationPhase.Analyze)
        {
            await ProcessAnalyzePhaseAsync(job, tableClient, cancellationToken);
        }
        else
        {
            await ProcessNormalizePhaseAsync(job, tableClient, cancellationToken);
        }
    }

    /// <summary>
    /// Phase 1: Download file and analyze loudness, then queue Phase 2.
    /// </summary>
    private async Task ProcessAnalyzePhaseAsync(NormalizationJob job, TableClient tableClient, CancellationToken cancellationToken)
    {
        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var pendingBlobPath = $"{job.FeedId}/pending/{job.JobId}/{job.FileName}";
        string? tempInputFile = null;

        try
        {
            tempInputFile = await DownloadPendingBlobAsync(containerClient, pendingBlobPath, job, tableClient, Analyzing, cancellationToken);

            // Analyze audio
            var inputFileSize = new FileInfo(tempInputFile).Length;
            _logger.LogInformation("Starting analysis for {FileName} (input size: {Size} bytes)", job.FileName, inputFileSize);
            _telemetryClient.Flush();

            var lastProgressUpdate = DateTime.MinValue;
            Task? pendingProgressUpdate = null;
            var analysisResult = await _normalizationService.AnalyzeAudioAsync(
                tempInputFile,
                progressCallback: progress =>
                {
                    var now = DateTime.UtcNow;
                    if (now - lastProgressUpdate < ProgressUpdateThrottle)
                    {
                        return;
                    }
                    lastProgressUpdate = now;

                    _logger.LogDebug("Progress callback: {Stage} {Percent}%", progress.Stage, progress.ProgressPercent);
                    pendingProgressUpdate = UpdateProgressAsync(tableClient, job.JobId, progress)
                        .ContinueWith(t => _logger.LogError(t.Exception, "Analysis progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
                },
                cancellationToken);

            if (analysisResult == null)
            {
                throw new InvalidOperationException("Audio analysis failed - no result produced");
            }

            // Wait for any pending progress update, then ensure 100% is written
            if (pendingProgressUpdate != null)
            {
                try { await pendingProgressUpdate; } catch { /* ignore */ }
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Analyzing,
                ProgressPercent = 100,
                Message = "Analysis complete"
            });

            _logger.LogInformation("Analysis complete for {FileName}: {InputLufs} LUFS, duration {Duration}",
                job.FileName, analysisResult.Analysis.InputI, analysisResult.Duration);

            // Queue the normalize phase
            var normalizeJob = job with
            {
                Phase = NormalizationPhase.Normalize,
                TotalDurationMs = (long)analysisResult.Duration.TotalMilliseconds,
                Analysis = LoudnessAnalysisData.FromAnalysis(analysisResult.Analysis)
            };

            var queueClient = _queueClient.GetQueueClient(QueueName);
            await queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var messageJson = JsonSerializer.Serialize(normalizeJob, JsonOptions);
            await queueClient.SendMessageAsync(messageJson, cancellationToken: cancellationToken);

            _logger.LogInformation("Queued normalize phase for job {JobId}", job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analyze phase failed for job {JobId}: {ErrorType} - {ErrorMessage}",
                job.JobId, ex.GetType().Name, ex.Message);
            _telemetryClient.Flush();

            var sanitizedError = SanitizeErrorMessage(ex);
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Failed, error: sanitizedError, cancellationToken: cancellationToken);

            throw;
        }
        finally
        {
            CleanupTempFile(tempInputFile);
        }
    }

    /// <summary>
    /// Phase 2: Download file, apply normalization with analysis data, upload, and create episode.
    /// </summary>
    private async Task ProcessNormalizePhaseAsync(NormalizationJob job, TableClient tableClient, CancellationToken cancellationToken)
    {
        if (job.Analysis == null || job.TotalDurationMs == null)
        {
            throw new InvalidOperationException("Normalize phase requires analysis data and duration");
        }

        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var pendingBlobPath = $"{job.FeedId}/pending/{job.JobId}/{job.FileName}";
        var finalBlobPath = $"{job.FeedId}/audio/{job.FileName}";
        var episodesJsonPath = $"{job.FeedId}/episodes.json";

        string? tempInputFile = null;
        string? normalizedFile = null;

        try
        {
            tempInputFile = await DownloadPendingBlobAsync(containerClient, pendingBlobPath, job, tableClient, Normalizing, cancellationToken);

            // Apply normalization with pre-computed analysis
            var totalDuration = TimeSpan.FromMilliseconds(job.TotalDurationMs.Value);
            var analysis = job.Analysis.ToLoudnessAnalysis();

            _logger.LogInformation("Starting normalization pass 2 for {FileName}", job.FileName);
            _telemetryClient.Flush();

            var lastProgressUpdate = DateTime.MinValue;
            Task? pendingProgressUpdate = null;
            normalizedFile = await _normalizationService.ApplyNormalizationAsync(
                tempInputFile,
                analysis,
                totalDuration,
                progressCallback: progress =>
                {
                    var now = DateTime.UtcNow;
                    if (now - lastProgressUpdate < ProgressUpdateThrottle)
                    {
                        return;
                    }
                    lastProgressUpdate = now;

                    _logger.LogDebug("Progress callback: {Stage} {Percent}%", progress.Stage, progress.ProgressPercent);
                    pendingProgressUpdate = UpdateProgressAsync(tableClient, job.JobId, progress)
                        .ContinueWith(t => _logger.LogError(t.Exception, "Normalization progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
                },
                cancellationToken);

            if (normalizedFile == null)
            {
                throw new InvalidOperationException("Normalization failed - no output file produced");
            }

            // Wait for any pending progress update, then ensure 100% is written
            if (pendingProgressUpdate != null)
            {
                try { await pendingProgressUpdate; } catch { /* ignore */ }
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Normalizing,
                ProgressPercent = 100,
                Message = "Normalization complete"
            });

            // Get duration from normalized file
            var mediaInfo = await FFProbe.AnalyseAsync(normalizedFile, cancellationToken: cancellationToken);
            var duration = mediaInfo.Duration;
            var normalizedFileSize = new FileInfo(normalizedFile).Length;

            // Upload normalized file
            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Finishing,
                ProgressPercent = 0,
                Message = "Finishing up"
            });

            _logger.LogDebug("Uploading normalized file to {FinalPath}", finalBlobPath);
            var finalBlob = containerClient.GetBlobClient(finalBlobPath);
            var uploadLastUpdate = DateTime.MinValue;
            var uploadProgress = new Progress<long>(bytesUploaded =>
            {
                var now = DateTime.UtcNow;
                var percent = normalizedFileSize > 0 ? (int)(bytesUploaded * 100 / normalizedFileSize) : 0;
                if (percent < 100 && now - uploadLastUpdate < ProgressUpdateThrottle)
                {
                    return;
                }
                uploadLastUpdate = now;

                _logger.LogDebug("Upload progress callback: {Percent}%", percent);
                _ = UpdateProgressAsync(tableClient, job.JobId, new()
                {
                    Stage = Finishing,
                    ProgressPercent = percent,
                    Message = "Finishing up"
                }).ContinueWith(t => _logger.LogError(t.Exception, "Upload progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
            });

            await using (var stream = File.OpenRead(normalizedFile))
            {
                await finalBlob.UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobUploadOptions
                {
                    ProgressHandler = uploadProgress
                }, cancellationToken);
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Finishing,
                ProgressPercent = 100,
                Message = "Almost done"
            });

            // Create episode entry
            var episode = new Episode
            {
                Id = job.EpisodeId,
                FeedId = job.FeedId,
                Title = job.Title,
                Description = job.Description,
                Summary = job.Summary,
                FileName = job.FileName,
                FileSize = normalizedFileSize,
                Duration = duration,
                PublishedDate = job.PublishedDate,
                Source = job.Source,
                UploadedAt = DateTime.UtcNow
            };

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Finishing,
                ProgressPercent = 0,
                Message = "Updating episode list"
            });

            await AddEpisodeToFeedAsync(containerClient, episodesJsonPath, episode, _logger, cancellationToken);

            // Update job status to Completed
            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Completed,
                ProgressPercent = 100,
                Message = "Normalization complete",
                TotalDuration = duration
            });

            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Completed, episodeId: job.EpisodeId, cancellationToken: cancellationToken);

            await RefreshAppServiceCacheAsync(job.FeedId, cancellationToken);

            // Delete pending blob
            _logger.LogDebug("Deleting pending blob {PendingPath}", pendingBlobPath);
            await containerClient.GetBlobClient(pendingBlobPath).DeleteIfExistsAsync(cancellationToken: cancellationToken);

            _logger.LogInformation("Normalization job {JobId} completed successfully. Episode {EpisodeId} created.", job.JobId, job.EpisodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Normalize phase failed for job {JobId}: {ErrorType} - {ErrorMessage}",
                job.JobId, ex.GetType().Name, ex.Message);
            _telemetryClient.Flush();

            var sanitizedError = SanitizeErrorMessage(ex);
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Failed, error: sanitizedError, cancellationToken: cancellationToken);

            throw;
        }
        finally
        {
            CleanupTempFile(tempInputFile);
            CleanupTempFile(normalizedFile);
        }
    }

    /// <summary>
    /// Downloads the pending blob to a temp file with progress updates.
    /// </summary>
    private async Task<string> DownloadPendingBlobAsync(
        BlobContainerClient containerClient,
        string pendingBlobPath,
        NormalizationJob job,
        TableClient tableClient,
        NormalizationStage stage,
        CancellationToken cancellationToken)
    {
        var pendingBlob = containerClient.GetBlobClient(pendingBlobPath);
        var blobProperties = await pendingBlob.GetPropertiesAsync(cancellationToken: cancellationToken);
        var downloadSize = blobProperties.Value.ContentLength;

        await UpdateProgressAsync(tableClient, job.JobId, new()
        {
            Stage = stage,
            ProgressPercent = 0,
            Message = "Preparing audio file"
        });

        var tempInputFile = Path.Combine(Path.GetTempPath(), $"{job.JobId}_{job.FileName}");

        _logger.LogDebug("Downloading pending blob to {TempFile}", tempInputFile);
        var downloadLastUpdate = DateTime.MinValue;
        var downloadProgress = new Progress<long>(bytesDownloaded =>
        {
            var now = DateTime.UtcNow;
            var percent = downloadSize > 0 ? (int)(bytesDownloaded * 100 / downloadSize) : 0;
            if (percent < 100 && now - downloadLastUpdate < ProgressUpdateThrottle)
            {
                return;
            }
            downloadLastUpdate = now;

            _logger.LogDebug("Download progress callback: {Percent}%", percent);
            _ = UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = stage,
                ProgressPercent = percent,
                Message = "Preparing audio file"
            }).ContinueWith(t => _logger.LogError(t.Exception, "Download progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        await pendingBlob.DownloadToAsync(tempInputFile, new() { ProgressHandler = downloadProgress }, cancellationToken);

        return tempInputFile;
    }

    private static string SanitizeErrorMessage(Exception ex)
    {
        return ex switch
        {
            InvalidOperationException => ex.Message,
            FileNotFoundException => "Input file not found",
            IOException => "File processing error",
            _ => "An internal error occurred during audio normalization"
        };
    }

    /// <summary>
    /// Handle messages that have failed all retry attempts.
    /// </summary>
    [Function("ProcessPoisonNormalizationJob")]
    public async Task ProcessPoisonJob([QueueTrigger($"{QueueName}-poison", Connection = "AzureWebJobsStorage")] string message, CancellationToken cancellationToken)
    {
        var job = JsonSerializer.Deserialize<NormalizationJob>(message);
        if (job == null)
        {
            _logger.LogError("Failed to deserialize poison queue message");
            return;
        }

        _logger.LogWarning("Processing poison queue message for job {JobId}", job.JobId);

        var tableClient = _tableClient.GetTableClient(TableName);

        // Ensure status is marked as Failed
        await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Failed, error: "Job failed after maximum retry attempts", cancellationToken: cancellationToken);

        // Clean up pending blob
        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var pendingBlobPath = $"{job.FeedId}/pending/{job.JobId}/{job.FileName}";
        var pendingBlob = containerClient.GetBlobClient(pendingBlobPath);
        await pendingBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        _logger.LogInformation("Poison job {JobId} cleanup completed", job.JobId);
    }

    private static async Task UpdateJobStatusAsync(
        TableClient tableClient,
        string jobId,
        string feedId,
        JobStatus status,
        string? episodeId = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        // Read existing entity to preserve progress fields
        JobStatusEntity entity;
        try
        {
            var existingResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
            entity = existingResponse.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            entity = new()
            {
                PartitionKey = "jobs",
                RowKey = jobId,
                FeedId = feedId,
                QueuedAt = DateTimeOffset.UtcNow
            };
        }

        entity.Status = status.ToString();
        entity.EpisodeId = episodeId;
        entity.Error = error;

        if (status is JobStatus.Completed or JobStatus.Failed)
        {
            entity.CompletedAt = DateTimeOffset.UtcNow;
        }

        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    private async Task UpdateProgressAsync(TableClient tableClient, string jobId, ProgressUpdate progress)
    {
        try
        {
            // Read existing entity to preserve QueuedAt and other fields
            var existingResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId);
            var entity = existingResponse.Value;

            // Update progress fields
            entity.Stage = progress.Stage.ToString();
            entity.ProgressPercent = progress.ProgressPercent;
            entity.ProgressMessage = progress.Message;
            entity.CurrentPositionMs = progress.CurrentPosition.HasValue ? (long)progress.CurrentPosition.Value.TotalMilliseconds : null;

            // Only update TotalDurationMs if provided (preserve existing value from Normalizing stage)
            if (progress.TotalDuration.HasValue)
            {
                entity.TotalDurationMs = (long)progress.TotalDuration.Value.TotalMilliseconds;
            }

            // Update Status based on stage
            entity.Status = progress.Stage switch
            {
                Completed => nameof(JobStatus.Completed),
                Failed => nameof(JobStatus.Failed),
                Queued => nameof(JobStatus.Queued),
                _ => nameof(JobStatus.Processing)
            };

            if (progress.Stage is Completed or Failed)
            {
                entity.CompletedAt = DateTimeOffset.UtcNow;
            }

            await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            _logger.LogDebug("Progress update saved: {Stage} {Percent}%", progress.Stage, progress.ProgressPercent);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // ETag conflict - entity was modified concurrently, ignore and continue
            _logger.LogDebug("Progress update conflict for job {JobId}, will retry on next update", jobId);
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Progress update throttled (429) for job {JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update progress for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Add episode to feed with blob lease for concurrency safety.
    /// Uses optimistic concurrency with retry on conflict.
    /// </summary>
    private static async Task AddEpisodeToFeedAsync(
        BlobContainerClient containerClient,
        string episodesJsonPath,
        Episode episode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var blob = containerClient.GetBlobClient(episodesJsonPath);
        var leaseClient = blob.GetBlobLeaseClient();

        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            string? leaseId = null;
            try
            {
                // Ensure blob exists before acquiring lease
                if (!await blob.ExistsAsync(cancellationToken))
                {
                    // Create empty episodes.json if it doesn't exist
                    await blob.UploadAsync(BinaryData.FromString("[]"), overwrite: false, cancellationToken);
                }

                // Acquire lease for exclusive access
                var lease = await leaseClient.AcquireAsync(LeaseDuration, cancellationToken: cancellationToken);
                leaseId = lease.Value.LeaseId;

                // Download current episodes
                var response = await blob.DownloadContentAsync(cancellationToken);
                var existingJson = response.Value.Content.ToString();
                var episodes = JsonSerializer.Deserialize<List<Episode>>(existingJson) ?? [];

                // Remove existing episode with same ID (update case)
                episodes.RemoveAll(e => e.Id == episode.Id);

                // Add new episode
                episodes.Add(episode);

                // Upload with lease condition
                var json = JsonSerializer.Serialize(episodes, new JsonSerializerOptions { WriteIndented = true });
                await blob.UploadAsync(
                    BinaryData.FromString(json),
                    new Azure.Storage.Blobs.Models.BlobUploadOptions
                    {
                        Conditions = new() { LeaseId = leaseId }
                    },
                    cancellationToken);

                logger.LogDebug("Successfully updated episodes.json for feed");
                return;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "LeaseAlreadyPresent" && attempt < maxRetries)
            {
                logger.LogWarning("Lease conflict on attempt {Attempt}, retrying...", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
            finally
            {
                // Release lease if we acquired it
                if (leaseId != null)
                {
                    try
                    {
                        await leaseClient.ReleaseAsync(cancellationToken: cancellationToken);
                    }
                    catch (RequestFailedException)
                    {
                        // Lease may have expired, ignore
                    }
                }
            }
        }

        throw new InvalidOperationException("Failed to update episodes.json after maximum retries");
    }

    private async Task RefreshAppServiceCacheAsync(string feedId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.AppServiceUrl))
        {
            _logger.LogWarning("AppServiceUrl not configured, skipping cache refresh");
            return;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrEmpty(_settings.InternalKey))
            {
                client.DefaultRequestHeaders.Add("X-Internal-Key", _settings.InternalKey);
            }

            var response = await client.PostAsync($"{_settings.AppServiceUrl}/api/internal/feeds/{feedId}/refresh", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Cache refresh successful for feed {FeedId}", feedId);
            }
            else
            {
                _logger.LogWarning("Cache refresh failed for feed {FeedId}: {StatusCode}", feedId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh cache for feed {FeedId}", feedId);
            // Don't fail the job if cache refresh fails - episode is already created
        }
    }

    private void CleanupTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temp file: {Path}", path);
        }
    }
}
