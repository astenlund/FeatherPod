using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FeatherPod.Functions;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FeatherPod.Tests;

/// <summary>
/// The normalization Function against Azurite Table Storage, with no App Service to signal (so a failure is recorded through the
/// direct fallback write): a recorded failure is not rethrown (so the queue does not redeliver it), a delivery or poison message of an
/// earlier attempt changes nothing on a retried job, and the cleanup's source claim is refused for a job changed since its read.
/// The jobs table is shared, so each test uses its own ids.
/// </summary>
[Collection("IntegrationTests")]
public sealed class NormalizationFunctionRetryTests : IAsyncLifetime
{
    private readonly TableClient _jobs = new TableServiceClient("UseDevelopmentStorage=true").GetTableClient(JobStorageNames.TableName);
    private readonly BlobContainerClient _container = new BlobServiceClient("UseDevelopmentStorage=true").GetBlobContainerClient($"test-normretry-{Guid.NewGuid():N}");
    private NormalizationFunction _function = null!;

    public async Task InitializeAsync()
    {
        await _jobs.CreateIfNotExistsAsync();
        await _container.CreateIfNotExistsAsync();
        var settings = Options.Create(new FunctionSettings { ContainerName = _container.Name, AppServiceUrl = string.Empty });
        _function = new NormalizationFunction(
            new BlobServiceClient("UseDevelopmentStorage=true"),
            new TableServiceClient("UseDevelopmentStorage=true"),
            null!,
            new StubHttpClientFactory(),
            new TelemetryClient(new TelemetryConfiguration()),
            settings,
            NullLogger<NormalizationFunction>.Instance);
    }

    public async Task DisposeAsync() => await _container.DeleteIfExistsAsync();

    [AzuriteFact]
    public async Task Delivery_FailureRecorded_IsNotRethrown()
    {
        // Arrange: the pending source is missing, so normalization fails right away
        var jobId = await AddJobAsync(attempt: 1, status: nameof(JobStatus.Queued));

        // Act
        var exception = await Record.ExceptionAsync(() => _function.ProcessNormalizationJob(JsonSerializer.Serialize(NewMessage(jobId, 1)), CancellationToken.None));

        // Assert
        Assert.Null(exception);
        var job = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        Assert.Equal(nameof(JobStatus.Failed), job.Status);
        Assert.False(string.IsNullOrEmpty(job.NormalizationError));
    }

    [AzuriteFact]
    public async Task Delivery_SignalTimesOut_RecordsTheFailureThroughTheFallbackWrite()
    {
        // Arrange: the App Service never answers the normalization-complete signal, so its HttpClient times out
        var jobId = await AddJobAsync(attempt: 1, status: nameof(JobStatus.Queued));
        var function = new NormalizationFunction(
            new BlobServiceClient("UseDevelopmentStorage=true"),
            new TableServiceClient("UseDevelopmentStorage=true"),
            null!,
            new TimingOutHttpClientFactory(),
            new TelemetryClient(new TelemetryConfiguration()),
            Options.Create(new FunctionSettings { ContainerName = _container.Name, AppServiceUrl = "http://app.invalid" }),
            NullLogger<NormalizationFunction>.Instance);

        // Act
        var exception = await Record.ExceptionAsync(() => function.ProcessNormalizationJob(JsonSerializer.Serialize(NewMessage(jobId, 1)), CancellationToken.None));

        // Assert
        Assert.Null(exception);
        var job = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        Assert.Equal(nameof(JobStatus.Failed), job.Status);
        Assert.True(job.NormalizationComplete);
    }

    [AzuriteFact]
    public async Task PoisonDelivery_FailureCouldNotBeRecorded_IsNotAcknowledged()
    {
        // Arrange: no App Service to signal, and the job table unreachable, so neither the signal nor the fallback records it
        var unreachableTables = new TableServiceClient(
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:1/devstoreaccount1",
            new TableClientOptions { Retry = { MaxRetries = 0 } });
        var function = new NormalizationFunction(
            new BlobServiceClient("UseDevelopmentStorage=true"),
            unreachableTables,
            null!,
            new StubHttpClientFactory(),
            new TelemetryClient(new TelemetryConfiguration()),
            Options.Create(new FunctionSettings { ContainerName = _container.Name, AppServiceUrl = string.Empty }),
            NullLogger<NormalizationFunction>.Instance);

        // Act
        var exception = await Record.ExceptionAsync(() => function.ProcessPoisonJob(JsonSerializer.Serialize(NewMessage("nfr-unrecordable", 1)), CancellationToken.None));

        // Assert: the delivery fails, so the queue redelivers it instead of dropping the unrecorded failure
        Assert.NotNull(exception);
    }

    [AzuriteFact]
    public async Task FallbackWrite_AfterTheSignalRecordedTheResult_ChangesNothing()
    {
        // Arrange: a slow signal was merged (normalization complete) but answered too late, so the Function falls back anyway
        var jobId = await AddJobAsync(attempt: 1, status: nameof(JobStatus.Processing));
        var recorded = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        recorded.NormalizationComplete = true;
        await _jobs.UpdateEntityAsync(recorded, recorded.ETag, TableUpdateMode.Replace);

        // Act
        var settled = await NormalizationFunction.WriteFallbackFailureAsync(_jobs, NewMessage(jobId, 1), "late fallback", CancellationToken.None);

        // Assert
        Assert.True(settled);
        var job = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        Assert.Equal(nameof(JobStatus.Processing), job.Status);
        Assert.Null(job.NormalizationError);
    }

    [AzuriteFact]
    public async Task Delivery_OfEarlierAttempt_NeitherMovesJobNorRecordsFailure()
    {
        // Arrange: the job was reset to attempt 2; a redelivered attempt-1 message arrives
        var jobId = await AddJobAsync(attempt: 2, status: nameof(JobStatus.Queued));

        // Act
        await _function.ProcessNormalizationJob(JsonSerializer.Serialize(NewMessage(jobId, 1)), CancellationToken.None);

        // Assert
        var job = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        Assert.Equal(nameof(JobStatus.Queued), job.Status);
        Assert.Null(job.NormalizationError);
    }

    [AzuriteFact]
    public async Task PoisonOfEarlierAttempt_ChangesNothing_WhileCurrentAttemptIsFailed()
    {
        // Arrange
        var retriedId = await AddJobAsync(attempt: 2, status: nameof(JobStatus.Queued));
        var currentId = await AddJobAsync(attempt: 2, status: nameof(JobStatus.Processing));

        // Act
        await _function.ProcessPoisonJob(JsonSerializer.Serialize(NewMessage(retriedId, 1)), CancellationToken.None);
        await _function.ProcessPoisonJob(JsonSerializer.Serialize(NewMessage(currentId, 2)), CancellationToken.None);

        // Assert
        Assert.Equal(nameof(JobStatus.Queued), (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, retriedId)).Value.Status);
        Assert.Equal(nameof(JobStatus.Failed), (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, currentId)).Value.Status);
    }

    [AzuriteFact]
    public async Task SourceSweepClaim_JobChangedSinceRead_Refused()
    {
        // Arrange: cleanup read the job failed; a normalization retry then reset it
        var jobId = await AddJobAsync(attempt: 1, status: nameof(JobStatus.Failed));
        var cleanupRead = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        var reset = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;
        reset.Attempt = 2;
        reset.Status = nameof(JobStatus.Queued);
        await _jobs.UpdateEntityAsync(reset, reset.ETag, TableUpdateMode.Replace);

        // Act
        var claimed = await CleanupFunction.TryClaimSourceSweepAsync(_jobs, cleanupRead, CancellationToken.None);

        // Assert
        Assert.False(claimed);
        Assert.Null((await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value.SourceSweptAt);
    }

    [AzuriteFact]
    public async Task SourceSweepClaim_UnchangedTerminalJob_Claimed()
    {
        // Arrange
        var jobId = await AddJobAsync(attempt: 1, status: nameof(JobStatus.Failed));
        var cleanupRead = (await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value;

        // Act
        var claimed = await CleanupFunction.TryClaimSourceSweepAsync(_jobs, cleanupRead, CancellationToken.None);

        // Assert
        Assert.True(claimed);
        Assert.NotNull((await _jobs.GetEntityAsync<JobStatusEntity>(JobStorageNames.JobsPartitionKey, jobId)).Value.SourceSweptAt);
    }

    private async Task<string> AddJobAsync(int attempt, string status)
    {
        var jobId = "nfr-" + Guid.NewGuid().ToString("N")[..10];
        await _jobs.AddEntityAsync(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = status,
            Attempt = attempt,
            FeedId = "nfr-feed",
            FileName = "missing.mp3",
            NormalizationStage = nameof(NormalizationStage.Queued),
            QueuedAt = DateTimeOffset.UtcNow,
        });

        return jobId;
    }

    private static NormalizationJob NewMessage(string jobId, int attempt) => new()
    {
        JobId = jobId,
        FeedId = "nfr-feed",
        FileName = "missing.mp3",
        OriginalFileSize = 3,
        EpisodeId = "ep-" + jobId,
        Title = "t",
        PublishedDate = DateTime.UtcNow,
        QueuedAt = DateTime.UtcNow,
        Attempt = attempt,
    };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Every request fails the way an elapsed HttpClient.Timeout does: a TaskCanceledException wrapping a TimeoutException.
    /// </summary>
    private sealed class TimingOutHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TimingOutHandler());

        private sealed class TimingOutHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());
        }
    }
}
