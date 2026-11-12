using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Startup recovery of transcriptions a previous process left Queued or Running. The scan itself is driven through
/// <c>RecoverInterruptedTranscriptionsAsync</c> without starting the service; the cases that start the service wait on a
/// deterministic signal (processing complete, or ExecuteAsync returned) before asserting.
/// </summary>
[Collection("Sequential")]
public class TranscriptionStartupRecoveryTests : IAsyncLifetime
{
    private readonly TranscriptionTestHarness _harness = new(useFast: true);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task InterruptedQueuedAndRunningJobs_AreResubmittedFromTheirEntities()
    {
        // Arrange
        _harness.Jobs.SetEntity(CreateEntity("job-queued", TranscriptionStatuses.Queued, nameof(JobStatus.Queued)));
        _harness.Jobs.SetEntity(CreateEntity("job-running", TranscriptionStatuses.Running, nameof(JobStatus.Processing)));

        // Act
        await _harness.RecoverAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Equal(["job-queued", "job-running"], submitted.Select(r => r.JobId).Order());
        var running = Assert.Single(submitted, r => r.JobId == "job-running");
        Assert.Equal("test-feed", running.FeedId);
        Assert.Equal("job-running.mp3", running.FileName);
        Assert.Equal("ep-job-running", running.EpisodeId);
    }

    [Fact]
    public async Task TerminalJobsAndSettledTranscriptions_AreNotResubmitted()
    {
        // Arrange
        _harness.Jobs.SetEntity(CreateEntity("job-failed", TranscriptionStatuses.Running, nameof(JobStatus.Failed)));
        _harness.Jobs.SetEntity(CreateEntity("job-cancelled", TranscriptionStatuses.Queued, nameof(JobStatus.Cancelled)));
        _harness.Jobs.SetEntity(CreateEntity("job-transcribed", TranscriptionStatuses.Completed, nameof(JobStatus.Processing)));
        _harness.Jobs.SetEntity(CreateEntity("job-untranscribed", null, nameof(JobStatus.Processing)));

        // Act
        await _harness.RecoverAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Empty(submitted);
    }

    [Fact]
    public async Task JobQueuedAfterTheServiceWasCreated_IsNotResubmitted()
    {
        // Arrange -- such a job was submitted to this instance's channel by its own upload request
        var entity = CreateEntity("job-new", TranscriptionStatuses.Queued, nameof(JobStatus.Queued));
        entity.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        _harness.Jobs.SetEntity(entity);

        // Act
        await _harness.RecoverAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Empty(submitted);
    }

    [Fact]
    public async Task JobMissingRequestFields_IsSkippedAndOthersStillResubmitted()
    {
        // Arrange
        var broken = CreateEntity("job-broken", TranscriptionStatuses.Running, nameof(JobStatus.Processing));
        broken.EpisodeId = null;
        _harness.Jobs.SetEntity(broken);
        _harness.Jobs.SetEntity(CreateEntity("job-ok", TranscriptionStatuses.Running, nameof(JobStatus.Processing)));

        // Act
        await _harness.RecoverAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Equal("job-ok", Assert.Single(submitted).JobId);
    }

    [Fact]
    public async Task FailedScan_IsSwallowedAndSubmitsNothing()
    {
        // Arrange
        _harness.Jobs.TranscriptionStatusQueryThrows = new InvalidOperationException("table unavailable");

        // Act
        var exception = await Record.ExceptionAsync(_harness.RecoverAsync);
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Null(exception);
        Assert.Empty(submitted);
    }

    [Fact]
    public async Task FailedScan_ServiceStillProcessesSubmittedRequests()
    {
        // Arrange
        _harness.Jobs.TranscriptionStatusQueryThrows = new InvalidOperationException("table unavailable");
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Still running.</v>\r\n";

        // Act
        await _harness.InitializeAsync();
        await _harness.SubmitAndWaitAsync();

        // Assert
        Assert.Equal(TranscriptionStatuses.Completed, _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!.TranscriptionStatus);
    }

    [Fact]
    public async Task SpeechUnavailable_StartupDoesNotResubmit()
    {
        // Arrange
        _harness.Speech.IsAvailable = false;
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.TranscriptionStatus = TranscriptionStatuses.Running;
        entity.EpisodeId = "ep-1";
        entity.QueuedAt = DateTimeOffset.UtcNow.AddHours(-1);
        _harness.Jobs.SetEntity(entity);

        // Act
        await _harness.InitializeAsync();
        await _harness.WaitForExecuteCompletionAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        Assert.Empty(submitted);
        Assert.Equal(TranscriptionStatuses.Running, _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!.TranscriptionStatus);
    }

    [Fact]
    public async Task InterruptedRunningJob_CompletesAfterServiceStart()
    {
        // Arrange
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.TranscriptionStatus = TranscriptionStatuses.Running;
        entity.EpisodeId = "ep-1";
        entity.QueuedAt = DateTimeOffset.UtcNow.AddHours(-1);
        _harness.Jobs.SetEntity(entity);
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Recovered.</v>\r\n";
        var processed = _harness.Jobs.WaitForProcessingCompleteAsync(TranscriptionTestHarness.JobId, TimeSpan.FromSeconds(5));

        // Act
        await _harness.InitializeAsync();
        await processed;

        // Assert
        Assert.Equal(TranscriptionStatuses.Completed, _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!.TranscriptionStatus);
        Assert.Equal(1, _harness.Speech.FastCallCount);
    }

    private static JobStatusEntity CreateEntity(string jobId, string? transcriptionStatus, string status) => new()
    {
        PartitionKey = JobStorageNames.JobsPartitionKey,
        RowKey = jobId,
        Status = status,
        FeedId = "test-feed",
        FileName = jobId + ".mp3",
        EpisodeId = "ep-" + jobId,
        TranscriptionStatus = transcriptionStatus,
        QueuedAt = DateTimeOffset.UtcNow.AddHours(-1),
    };
}
