using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FeatherPod.Functions;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class CleanupFunctionTests : IAsyncLifetime
{
    private readonly BlobServiceClient _blobClient;
    private readonly TableServiceClient _tableClient;
    private readonly TableClient _tableClientInstance;
    private readonly BlobContainerClient _containerClient;
    private readonly CleanupFunction _function;
    private readonly string _containerName;
    private readonly string _tableName;

    public CleanupFunctionTests()
    {
        _blobClient = new BlobServiceClient("UseDevelopmentStorage=true");
        _tableClient = new TableServiceClient("UseDevelopmentStorage=true");
        _containerName = $"test-cleanup-{Guid.NewGuid():N}";
        _tableName = $"cleanup{Guid.NewGuid():N}";
        _containerClient = _blobClient.GetBlobContainerClient(_containerName);
        _tableClientInstance = _tableClient.GetTableClient(_tableName);

        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 7,
            OrphanedBlobRetentionDays = 1
        });

        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        _function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);
    }

    public async Task InitializeAsync()
    {
        await _containerClient.CreateIfNotExistsAsync();
        await _tableClientInstance.CreateIfNotExistsAsync();
    }

    public async Task DisposeAsync()
    {
        await _containerClient.DeleteIfExistsAsync();
        await _tableClientInstance.DeleteAsync();
    }

    [AzuriteFact]
    public async Task CleanupOldJobs_DeletesCompletedJobsOlderThanRetention()
    {
        // Arrange - create an old completed job
        var oldJob = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = Guid.NewGuid().ToString(),
            Status = nameof(JobStatus.Completed),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-10)
        };
        await _tableClientInstance.AddEntityAsync(oldJob);

        // Act
        var deleted = await _function.CleanupOldJobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(1, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOldJobs_PreservesRecentJobs()
    {
        // Arrange - create a recent completed job
        var recentJob = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = Guid.NewGuid().ToString(),
            Status = nameof(JobStatus.Completed),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        await _tableClientInstance.AddEntityAsync(recentJob);

        // Act
        var deleted = await _function.CleanupOldJobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOldJobs_DeletesStuckJobsOlderThan3xRetention()
    {
        // Arrange - create a very old stuck job (no CompletedAt)
        var stuckJob = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = Guid.NewGuid().ToString(),
            Status = nameof(JobStatus.Processing),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-25)
        };
        await _tableClientInstance.AddEntityAsync(stuckJob);

        // Act
        var deleted = await _function.CleanupOldJobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(1, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOldJobs_PreservesJobsLessThanOneHourOld_EvenWithZeroRetention()
    {
        // Arrange - create a job completed 30 minutes ago with 0-day retention
        var recentJob = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = Guid.NewGuid().ToString(),
            Status = nameof(JobStatus.Completed),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-45),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
        await _tableClientInstance.AddEntityAsync(recentJob);

        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 0,
            OrphanedBlobRetentionDays = 1
        });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOldJobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOrphanedBlobs_DeletesBlobsWithNoJob()
    {
        // Arrange - create a pending blob with no corresponding job
        var blobPath = "test-feed/pending/nonexistent-job-id/test.mp3";
        await _containerClient.GetBlobClient(blobPath).UploadAsync(BinaryData.FromString("fake audio"));

        // Use 0-day retention so recently-created blob is eligible
        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 7,
            OrphanedBlobRetentionDays = 0
        });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOrphanedBlobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(1, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOrphanedBlobs_PreservesBlobsWithActiveJob()
    {
        // Arrange - create a pending blob with an active (Processing) job
        var jobId = Guid.NewGuid().ToString();
        var blobPath = $"test-feed/pending/{jobId}/test.mp3";
        await _containerClient.GetBlobClient(blobPath).UploadAsync(BinaryData.FromString("fake audio"));

        var job = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Processing),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow
        };
        await _tableClientInstance.AddEntityAsync(job);

        // Use 0-day retention so blob age doesn't short-circuit -- tests the job status check
        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 7,
            OrphanedBlobRetentionDays = 0
        });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOrphanedBlobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
    }

    [AzuriteFact]
    public async Task CleanupOrphanedBlobs_PreservesBlobOfJobResetForNormalizationRetry()
    {
        // Arrange - a job that failed in normalization a day ago, then reset by a normalization retry
        var jobId = Guid.NewGuid().ToString();
        var blobPath = $"test-feed/pending/{jobId}/test.mp3";
        await _containerClient.GetBlobClient(blobPath).UploadAsync(BinaryData.FromString("fake audio"));

        var job = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Failed),
            FeedId = "test-feed",
            NormalizationComplete = true,
            NormalizationError = "FFmpeg crashed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        FeatherPod.Server.Services.JobRetryService.ResetForNormalizationRetry(job, speechAvailable: false, DateTimeOffset.UtcNow);
        await _tableClientInstance.AddEntityAsync(job);

        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 7,
            OrphanedBlobRetentionDays = 0
        });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOrphanedBlobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(0, deleted);
        Assert.True(await _containerClient.GetBlobClient(blobPath).ExistsAsync());
    }

    [AzuriteFact]
    public async Task CleanupOrphanedBlobs_StuckActiveJob_DeletesOnlyThroughTheJobRowClaim()
    {
        // Arrange - an active job stuck for longer than the retention window
        var jobId = Guid.NewGuid().ToString();
        var blobPath = $"test-feed/pending/{jobId}/test.mp3";
        await _containerClient.GetBlobClient(blobPath).UploadAsync(BinaryData.FromString("fake audio"));
        await _tableClientInstance.AddEntityAsync(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Processing),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        var settings = Options.Create(new FunctionSettings { ContainerName = _containerName, JobRetentionDays = 7, OrphanedBlobRetentionDays = 0 });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOrphanedBlobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert - the deletion was claimed on the job row, the claim a reset of that row would have refused
        Assert.Equal(1, deleted);
        var job = (await _tableClientInstance.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        Assert.NotNull(job.SourceSweptAt);
    }

    [AzuriteFact]
    public async Task CleanupOrphanedBlobs_DeletesBlobsForCompletedJobs()
    {
        // Arrange - create a pending blob where job is already completed (orphaned cleanup failure)
        var jobId = Guid.NewGuid().ToString();
        var blobPath = $"test-feed/pending/{jobId}/test.mp3";
        await _containerClient.GetBlobClient(blobPath).UploadAsync(BinaryData.FromString("fake audio"));

        var job = new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Completed),
            FeedId = "test-feed",
            QueuedAt = DateTimeOffset.UtcNow.AddDays(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        await _tableClientInstance.AddEntityAsync(job);

        // Use 0-day retention so recently-created blob is eligible
        var settings = Options.Create(new FunctionSettings
        {
            ContainerName = _containerName,
            JobRetentionDays = 7,
            OrphanedBlobRetentionDays = 0
        });
        var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<CleanupFunction>();
        var function = new CleanupFunction(_blobClient, _tableClient, new StubHttpClientFactory(), settings, logger);

        // Act
        var deleted = await function.CleanupOrphanedBlobsAsync(_tableClientInstance, CancellationToken.None);

        // Assert
        Assert.Equal(1, deleted);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
