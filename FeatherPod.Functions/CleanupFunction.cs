using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FeatherPod.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeatherPod.Functions;

/// <summary>
/// Timer-triggered function that cleans up old normalization job entries
/// from Table Storage and orphaned pending blobs from Blob Storage.
/// </summary>
public class CleanupFunction
{
    private readonly BlobServiceClient _blobClient;
    private readonly TableServiceClient _tableClient;
    private readonly FunctionSettings _settings;
    private readonly ILogger<CleanupFunction> _logger;

    private const string TableName = "normalizationjobs";

    public CleanupFunction(
        BlobServiceClient blobClient,
        TableServiceClient tableClient,
        IOptions<FunctionSettings> settings,
        ILogger<CleanupFunction> logger)
    {
        _blobClient = blobClient;
        _tableClient = tableClient;
        _settings = settings.Value;
        _logger = logger;
    }

    [Function("CleanupNormalizationJobs")]
    public async Task CleanupNormalizationJobs(
        [TimerTrigger("%CleanupSchedule%")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting normalization cleanup. Job retention: {JobDays} days, Orphaned blob retention: {BlobDays} days",
            _settings.JobRetentionDays, _settings.OrphanedBlobRetentionDays);

        var tableClient = _tableClient.GetTableClient(TableName);
        await tableClient.CreateIfNotExistsAsync(cancellationToken);

        var deletedJobs = await CleanupOldJobsAsync(tableClient, cancellationToken);
        var deletedBlobs = await CleanupOrphanedBlobsAsync(tableClient, cancellationToken);

        _logger.LogInformation("Cleanup complete. Deleted {JobCount} job entries and {BlobCount} orphaned blobs",
            deletedJobs, deletedBlobs);
    }

    /// <summary>
    /// Delete job status entries where CompletedAt is older than JobRetentionDays.
    /// </summary>
    internal async Task<int> CleanupOldJobsAsync(TableClient tableClient, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.JobRetentionDays);
        var deletedCount = 0;

        // Query for completed/failed jobs older than cutoff
        var filter = $"PartitionKey eq 'jobs' and CompletedAt lt datetime'{cutoff:yyyy-MM-ddTHH:mm:ssZ}'";

        await foreach (var entity in tableClient.QueryAsync<JobStatusEntity>(filter, cancellationToken: cancellationToken))
        {
            try
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken);
                deletedCount++;
                _logger.LogDebug("Deleted job entry {JobId} (completed {CompletedAt})", entity.RowKey, entity.CompletedAt);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted (concurrent cleanup), skip
            }
        }

        // Also clean up stuck jobs (no CompletedAt but QueuedAt is very old - e.g. 3x retention)
        var stuckCutoff = DateTimeOffset.UtcNow.AddDays(-_settings.JobRetentionDays * 3);
        var stuckFilter = $"PartitionKey eq 'jobs' and QueuedAt lt datetime'{stuckCutoff:yyyy-MM-ddTHH:mm:ssZ}'";

        await foreach (var entity in tableClient.QueryAsync<JobStatusEntity>(stuckFilter, cancellationToken: cancellationToken))
        {
            // Only delete if still not completed (stuck in Processing/Queued)
            if (entity.CompletedAt != null)
            {
                continue;
            }

            try
            {
                await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag, cancellationToken);
                deletedCount++;
                _logger.LogWarning("Deleted stuck job entry {JobId} (queued {QueuedAt}, status {Status})", entity.RowKey, entity.QueuedAt, entity.Status);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Already deleted
            }
        }

        return deletedCount;
    }

    /// <summary>
    /// Delete pending blobs that have no corresponding active job or whose job is older than retention.
    /// Discovers feed IDs using hierarchical blob listing, then scans each feed's pending/ prefix.
    /// </summary>
    internal async Task<int> CleanupOrphanedBlobsAsync(TableClient tableClient, CancellationToken cancellationToken)
    {
        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.OrphanedBlobRetentionDays);
        var deletedCount = 0;

        // Discover feed IDs by listing top-level "directories" using delimiter
        var feedIds = new List<string>();
        await foreach (var page in containerClient.GetBlobsByHierarchyAsync(delimiter: "/", cancellationToken: cancellationToken).AsPages())
        {
            foreach (var prefix in page.Values.Where(v => v.IsPrefix))
            {
                // Prefix looks like "feedId/" — strip trailing slash
                feedIds.Add(prefix.Prefix.TrimEnd('/'));
            }
        }

        // For each feed, scan its pending/ prefix
        foreach (var feedId in feedIds)
        {
            await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: $"{feedId}/pending/", cancellationToken: cancellationToken))
            {
                // Parse jobId from path: {feedId}/pending/{jobId}/{fileName}
                var parts = blobItem.Name.Split('/');
                if (parts.Length < 4 || parts[1] != "pending")
                {
                    continue;
                }

                var jobId = parts[2];

                // Check if blob is old enough to be considered orphaned
                var blobCreatedOn = blobItem.Properties.CreatedOn;
                if (blobCreatedOn.HasValue && blobCreatedOn.Value > cutoff)
                {
                    // Blob is too recent, skip (job might still be processing)
                    continue;
                }

                // Check if job exists and is still active
                var shouldDelete = false;
                try
                {
                    var jobResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
                    var job = jobResponse.Value;

                    // If job is completed, failed, or cancelled, pending blob is orphaned (should have been deleted)
                    if (job.GetJobStatus() is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                    {
                        shouldDelete = true;
                    }
                    // If job is still active but very old, it's stuck - clean up
                    else if (job.QueuedAt.HasValue && job.QueuedAt.Value < DateTimeOffset.UtcNow.AddDays(-_settings.JobRetentionDays))
                    {
                        shouldDelete = true;
                    }
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // No job entry exists - blob is orphaned
                    shouldDelete = true;
                }

                if (shouldDelete)
                {
                    try
                    {
                        await containerClient.GetBlobClient(blobItem.Name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
                        deletedCount++;
                        _logger.LogInformation("Deleted orphaned pending blob: {BlobPath}", blobItem.Name);
                    }
                    catch (Azure.RequestFailedException ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned blob: {BlobPath}", blobItem.Name);
                    }
                }
            }
        }

        return deletedCount;
    }
}
