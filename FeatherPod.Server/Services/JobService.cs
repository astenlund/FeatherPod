using System.Text.Json;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Service for managing normalization jobs using Azure Queue and Table Storage.
/// </summary>
public class JobService : IJobService
{
    private readonly QueueClient _queueClient;
    private readonly TableClient _tableClient;
    private readonly ILogger<JobService> _logger;

    private const string QueueName = "normalization-jobs";
    private const string TableName = "normalizationjobs";

    public JobService(IConfiguration config, ILogger<JobService> logger)
    {
        _logger = logger;

        var azureConfig = config.GetSection("Azure").Get<AzureStorageConfig>()!;

        // Create clients using same auth pattern as BlobStorageService
        // Disable Base64 encoding so Azure Functions receives plain JSON
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };

        if (!string.IsNullOrEmpty(azureConfig.ConnectionString))
        {
            _queueClient = new(azureConfig.ConnectionString, QueueName, queueOptions);
            _tableClient = new(azureConfig.ConnectionString, TableName);
            _logger.LogInformation("Using connection string for queue/table storage authentication");
        }
        else if (!string.IsNullOrEmpty(azureConfig.AccountName))
        {
            var credential = new DefaultAzureCredential();
            var queueUri = new Uri($"https://{azureConfig.AccountName}.queue.core.windows.net/{QueueName}");
            var tableUri = new Uri($"https://{azureConfig.AccountName}.table.core.windows.net");

            _queueClient = new(queueUri, credential, queueOptions);
            _tableClient = new(tableUri, TableName, credential);
            _logger.LogInformation("Using managed identity for queue/table storage authentication");
        }
        else
        {
            throw new InvalidOperationException("Azure storage configuration requires either ConnectionString or AccountName");
        }
    }

    /// <summary>
    /// Initialize queue and table (create if not exists).
    /// Called during app startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _queueClient.CreateIfNotExistsAsync();
        await _tableClient.CreateIfNotExistsAsync();
        _logger.LogInformation("Job service initialized. Queue: {Queue}, Table: {Table}", QueueName, TableName);
    }

    public async Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(job);
        await _queueClient.SendMessageAsync(json, cancellationToken);
        _logger.LogInformation("Queued normalization job {JobId} for {FeedId}/{FileName}", job.JobId, job.FeedId, job.FileName);
    }

    public async Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task CreateJobStatusAsync(string jobId, string feedId, CancellationToken cancellationToken = default)
    {
        var entity = JobStatusEntity.CreateQueued(jobId, feedId);
        try
        {
            await _tableClient.AddEntityAsync(entity, cancellationToken);
            _logger.LogDebug("Created job status entry for {JobId}", jobId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            // Job already exists (duplicate queue message) - preserve original QueuedAt
            _logger.LogDebug("Job status entry already exists for {JobId}, skipping creation", jobId);
        }
    }

    public async Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
                var entity = response.Value;

                // Already in terminal state — not cancellable
                if (entity.GetJobStatus() is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                {
                    return null;
                }

                entity.Status = nameof(JobStatus.Cancelled);
                entity.Stage = nameof(NormalizationStage.Cancelled);
                entity.CompletedAt = DateTimeOffset.UtcNow;

                await _tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
                _logger.LogInformation("Cancelled job {JobId}", jobId);

                return entity;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412 && attempt < maxRetries)
            {
                // ETag conflict — job was modified concurrently, retry
                _logger.LogDebug("ETag conflict cancelling job {JobId}, attempt {Attempt}", jobId, attempt);
            }
        }

        // Final attempt: re-read to see if it became terminal
        var finalResponse = await _tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
        var finalEntity = finalResponse.Value;
        if (finalEntity.GetJobStatus() is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            return null;
        }

        _logger.LogWarning("Failed to cancel job {JobId} after {MaxRetries} attempts", jobId, maxRetries);

        return null;
    }
}
