using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FFMpegCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeatherPod.Functions;

/// <summary>
/// Azure Function that processes audio normalization jobs from the queue.
/// </summary>
public class NormalizationFunction
{
    private readonly BlobServiceClient _blobClient;
    private readonly TableServiceClient _tableClient;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FunctionSettings _settings;
    private readonly ILogger<NormalizationFunction> _logger;

    private const string TableName = "normalizationjobs";
    private const string QueueName = "normalization-jobs";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    public NormalizationFunction(
        BlobServiceClient blobClient,
        TableServiceClient tableClient,
        IAudioNormalizationService normalizationService,
        IHttpClientFactory httpClientFactory,
        IOptions<FunctionSettings> settings,
        ILogger<NormalizationFunction> logger)
    {
        _blobClient = blobClient;
        _tableClient = tableClient;
        _normalizationService = normalizationService;
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    [Function("ProcessNormalizationJob")]
    public async Task ProcessNormalizationJob([QueueTrigger(QueueName, Connection = "AzureWebJobsStorage")] string message, CancellationToken cancellationToken)
    {
        var job = JsonSerializer.Deserialize<NormalizationJob>(message)
            ?? throw new InvalidOperationException("Failed to deserialize normalization job");

        _logger.LogInformation("Processing normalization job {JobId} for {FeedId}/{FileName}",
            job.JobId, job.FeedId, job.FileName);

        var tableClient = _tableClient.GetTableClient(TableName);
        await tableClient.CreateIfNotExistsAsync(cancellationToken);

        // Update status to Processing
        await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Processing, cancellationToken: cancellationToken);

        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var pendingBlobPath = $"{job.FeedId}/pending/{job.JobId}/{job.FileName}";
        var finalBlobPath = $"{job.FeedId}/audio/{job.FileName}";
        var episodesJsonPath = $"{job.FeedId}/episodes.json";

        string? tempInputFile = null;
        string? normalizedFile = null;

        try
        {
            // Download pending blob to temp file
            var pendingBlob = containerClient.GetBlobClient(pendingBlobPath);
            tempInputFile = Path.Combine(Path.GetTempPath(), $"{job.JobId}_{job.FileName}");

            _logger.LogDebug("Downloading pending blob to {TempFile}", tempInputFile);
            await pendingBlob.DownloadToAsync(tempInputFile, cancellationToken);

            // Normalize audio
            _logger.LogInformation("Starting normalization for {FileName}", job.FileName);
            normalizedFile = await _normalizationService.NormalizeAudioAsync(tempInputFile, cancellationToken);

            if (normalizedFile == null)
            {
                throw new InvalidOperationException("Normalization failed - no output file produced");
            }

            // Get duration from normalized file
            var mediaInfo = await FFProbe.AnalyseAsync(normalizedFile, cancellationToken: cancellationToken);
            var duration = mediaInfo.Duration;

            // Upload normalized file to final location
            _logger.LogDebug("Uploading normalized file to {FinalPath}", finalBlobPath);
            var finalBlob = containerClient.GetBlobClient(finalBlobPath);
            await using (var stream = File.OpenRead(normalizedFile))
            {
                await finalBlob.UploadAsync(stream, overwrite: true, cancellationToken);
            }

            // Get the file size of the normalized file for the episode
            var normalizedFileSize = new FileInfo(normalizedFile).Length;

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
                PublishedDate = job.PublishedDate
            };

            // Update episodes.json with lease for concurrency safety
            await AddEpisodeToFeedAsync(containerClient, episodesJsonPath, episode, _logger, cancellationToken);

            // Update job status to Completed
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Completed,
                episodeId: job.EpisodeId, cancellationToken: cancellationToken);

            // Call App Service refresh endpoint
            await RefreshAppServiceCacheAsync(job.FeedId, cancellationToken);

            // Delete pending blob
            _logger.LogDebug("Deleting pending blob {PendingPath}", pendingBlobPath);
            await pendingBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);

            _logger.LogInformation("Normalization job {JobId} completed successfully. Episode {EpisodeId} created.",
                job.JobId, job.EpisodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Normalization job {JobId} failed", job.JobId);

            // Sanitize error message to avoid exposing internal details (file paths, connection strings, etc.)
            var sanitizedError = ex switch
            {
                InvalidOperationException => ex.Message,
                FileNotFoundException => "Input file not found",
                IOException => "File processing error",
                _ => "An internal error occurred during audio normalization"
            };

            // Update job status to Failed
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Failed,
                error: sanitizedError, cancellationToken: cancellationToken);

            // Delete pending blob on failure
            var pendingBlob = containerClient.GetBlobClient(pendingBlobPath);
            await pendingBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);

            throw; // Re-throw to trigger retry/poison queue
        }
        finally
        {
            // Cleanup temp files
            CleanupTempFile(tempInputFile);
            CleanupTempFile(normalizedFile);
        }
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
        // Read existing entity to preserve QueuedAt
        DateTimeOffset? queuedAt = null;
        try
        {
            var existingResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
            queuedAt = existingResponse.Value.QueuedAt;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Entity doesn't exist yet, QueuedAt will be set to now
            queuedAt = DateTimeOffset.UtcNow;
        }

        var entity = new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = jobId,
            FeedId = feedId,
            Status = status.ToString(),
            EpisodeId = episodeId,
            Error = error,
            QueuedAt = queuedAt
        };

        if (status is JobStatus.Completed or JobStatus.Failed)
        {
            entity.CompletedAt = DateTimeOffset.UtcNow;
        }

        await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
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
                    await blob.UploadAsync(BinaryData.FromString("[]"), overwrite: false, cancellationToken: cancellationToken);
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
