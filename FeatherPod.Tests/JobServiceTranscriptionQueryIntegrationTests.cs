using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FeatherPod.Tests;

/// <summary>
/// Runs <see cref="JobService.GetActiveJobsByTranscriptionStatusAsync"/> against Azurite Table Storage, so the OData
/// filter it builds is exercised by a real table. The jobs table is shared, so assertions look only at this test's ids.
/// </summary>
[Collection("IntegrationTests")]
public class JobServiceTranscriptionQueryIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [AzuriteFact]
    public async Task GetActiveJobsByTranscriptionStatus_ReturnsOnlyActiveJobsWithAMatchingStatus()
    {
        // Arrange
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        await jobs.InitializeAsync();
        var prefix = "txq-" + Guid.NewGuid().ToString("N")[..8];
        var queued = await CreateAsync(jobs, prefix + "-queued", TranscriptionStatuses.Queued);
        var running = await CreateAsync(jobs, prefix + "-running", TranscriptionStatuses.Running);
        var completed = await CreateAsync(jobs, prefix + "-completed", TranscriptionStatuses.Completed);
        var none = await CreateAsync(jobs, prefix + "-none", null);
        var terminal = await CreateAsync(jobs, prefix + "-terminal", TranscriptionStatuses.Running);
        await jobs.UpdateJobStatusAsync(terminal, e => e.Status = nameof(JobStatus.Failed));

        // Act
        var found = await jobs.GetActiveJobsByTranscriptionStatusAsync([TranscriptionStatuses.Queued, TranscriptionStatuses.Running]);

        // Assert
        var ours = found.Select(e => e.RowKey).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        Assert.Equal([queued, running], ours);
        Assert.DoesNotContain(completed, found.Select(e => e.RowKey));
        Assert.DoesNotContain(none, found.Select(e => e.RowKey));
    }

    [AzuriteFact]
    public async Task GetActiveJobsByTranscriptionStatus_WithNoStatuses_ReturnsNothing()
    {
        // Arrange
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        await jobs.InitializeAsync();

        // Act
        var found = await jobs.GetActiveJobsByTranscriptionStatusAsync([]);

        // Assert
        Assert.Empty(found);
    }

    private static async Task<string> CreateAsync(IJobService jobs, string jobId, string? transcriptionStatus)
    {
        await jobs.CreateJobStatusAsync(new CreateJobOptions
        {
            JobId = jobId,
            FeedId = "txq-feed",
            FileName = jobId + ".mp3",
            EpisodeId = "ep-" + jobId,
            TranscriptionStatus = transcriptionStatus,
        });

        return jobId;
    }
}
