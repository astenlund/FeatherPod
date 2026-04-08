using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Tests for the JobCompletionService join logic.
/// Uses an in-memory IJobService stub. Verifies entity state transitions
/// rather than intercepting episode creation (EpisodeService is sealed).
/// </summary>
[Collection("Sequential")]
public class JobCompletionServiceTests
{
    private readonly InMemoryJobService _jobService = new();
    private readonly JobCompletionService _service;

    public JobCompletionServiceTests()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "jcs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var blobService = new TestBlobStorageService(tempDir);
        var episodeService = new EpisodeService(blobService, NullLogger<EpisodeService>.Instance);

        _service = new JobCompletionService(
            _jobService,
            episodeService,
            blobService,
            new NullJobProgressChannel(),
            new NullFeedEventChannel(),
            new StubPushNotificationService(new StubBlobService(), episodeService, _jobService),
            NullLogger<JobCompletionService>.Instance);
    }

    [Fact]
    public async Task NormalizationCompletesFirst_TranscriptionStillRunning_NoEpisodeYet()
    {
        // Arrange
        var jobId = "job-norm-first";
        CreateTestEntity(jobId, transcriptionStatus: "Running");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 1024,
            AudioDurationMs = 60000
        }, CancellationToken.None);

        // Assert - job still Processing (transcription not done)
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(true, entity.NormalizationComplete);
        Assert.Equal(nameof(JobStatus.Processing), entity.Status);
        Assert.Null(entity.EpisodeId);
    }

    [Fact]
    public async Task BothTracksComplete_EpisodeCreated()
    {
        // Arrange
        var jobId = "job-both-done";
        CreateTestEntity(jobId, transcriptionStatus: "Completed");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 2048,
            AudioDurationMs = 120000
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
        Assert.NotNull(entity.CompletedAt);
    }

    [Fact]
    public async Task TranscriptionDisabled_NormalizationAlone_CreatesEpisode()
    {
        // Arrange
        var jobId = "job-no-trans";
        CreateTestEntity(jobId, transcriptionStatus: null);

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 512,
            AudioDurationMs = 30000
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
    }

    [Fact]
    public async Task NormalizationFails_JobMarkedFailed()
    {
        // Arrange
        var jobId = "job-norm-fail";
        CreateTestEntity(jobId, transcriptionStatus: null);

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = false,
            Error = "FFmpeg crashed"
        }, CancellationToken.None);

        // Assert
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Failed), entity.Status);
        Assert.Null(entity.EpisodeId);
        Assert.Equal("FFmpeg crashed", entity.NormalizationError);
    }

    [Fact]
    public async Task TranscriptionFails_EpisodeStillCreated()
    {
        // Arrange
        var jobId = "job-trans-fail";
        CreateTestEntity(jobId, transcriptionStatus: "Failed");

        // Act
        await _service.HandleNormalizationCompleteAsync(jobId, new NormalizationCompleteRequest
        {
            Success = true,
            NormalizedFileSize = 1024,
            AudioDurationMs = 60000
        }, CancellationToken.None);

        // Assert - episode created (normalization succeeded, transcription failure doesn't block)
        var entity = _jobService.GetEntity(jobId)!;
        Assert.Equal(nameof(JobStatus.Completed), entity.Status);
        Assert.NotNull(entity.EpisodeId);
    }

    private void CreateTestEntity(string jobId, string? transcriptionStatus)
    {
        _jobService.SetEntity(new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = jobId,
            Status = nameof(JobStatus.Processing),
            FeedId = "test-feed",
            FileName = "test.mp3",
            Title = "Test Episode",
            Description = "Test description",
            Source = "Browser",
            OriginalFileSize = 5000,
            TranscriptionStatus = transcriptionStatus,
            QueuedAt = DateTimeOffset.UtcNow
        });
    }

}
