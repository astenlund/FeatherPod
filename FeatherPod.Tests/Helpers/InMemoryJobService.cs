using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// Thread-safe in-memory <see cref="IJobService"/> for unit tests. Mutations from
/// background services are safe; tests can also seed entities directly via
/// <see cref="SetEntity"/> and observe state via <see cref="GetEntity"/>.
///
/// <para>
/// <see cref="WaitForProcessingCompleteAsync"/> exposes a <see cref="TaskCompletionSource"/>
/// that fires the next time <see cref="GetJobStatusAsync"/> is called for a given job id.
/// In <c>TranscriptionBackgroundService</c>, the only call to <see cref="GetJobStatusAsync"/>
/// happens inside <c>JobCompletionService.TryCompleteJobAsync</c>, which runs *after* the
/// transcription try/finally block has fully completed (terminal status written, DeleteAsync
/// called for quota cleanup, temp files cleaned up). This makes it a deterministic
/// "processing complete" signal regardless of whether other fakes return synchronously.
/// </para>
/// </summary>
public sealed class InMemoryJobService : IJobService
{
    private readonly Dictionary<string, JobStatusEntity> _entities = new();
    private readonly Dictionary<string, TaskCompletionSource> _processingCompleteSignals = new();
    private readonly object _lock = new();

    public void SetEntity(JobStatusEntity entity)
    {
        lock (_lock)
        {
            _entities[entity.RowKey] = entity;
        }
    }

    public JobStatusEntity? GetEntity(string jobId)
    {
        lock (_lock)
        {
            return _entities.GetValueOrDefault(jobId);
        }
    }

    /// <summary>
    /// Wait until the next <see cref="GetJobStatusAsync"/> call for <paramref name="jobId"/>.
    /// In the <c>TranscriptionBackgroundService</c> pipeline this fires after the finally
    /// block has run, so observable side effects (terminal status, DeleteAsync, temp file cleanup)
    /// are guaranteed to have completed.
    /// </summary>
    public async Task WaitForProcessingCompleteAsync(string jobId, TimeSpan timeout)
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _processingCompleteSignals[jobId] = tcs;
        }

        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException($"Timed out waiting for processing to complete for job {jobId}")));
        await tcs.Task;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? signal;
        JobStatusEntity? entity;
        lock (_lock)
        {
            entity = _entities.GetValueOrDefault(jobId);
            _processingCompleteSignals.Remove(jobId, out signal);
        }

        signal?.TrySetResult();

        return Task.FromResult(entity);
    }

    public Task CreateJobStatusAsync(
        string jobId,
        string feedId,
        string? fileName = null,
        string? title = null,
        string? progressMode = null,
        int? progressIntervalMs = null,
        string? description = null,
        string? summary = null,
        DateTimeOffset? publishedDate = null,
        string? source = null,
        long? originalFileSize = null,
        string? episodeId = null,
        string? transcriptionStatus = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);

    public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default) => Task.FromResult<List<JobStatusEntity>>([]);

    public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobStatusEntity?>(null);

    public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            mutate(entity);

            return Task.FromResult<JobStatusEntity?>(entity);
        }
    }

    public Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            configure(entity);

            return Task.FromResult<JobStatusEntity?>(entity);
        }
    }

    public Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                throw new Azure.RequestFailedException(404, "Not found");
            }

            configure(entity);

            return Task.CompletedTask;
        }
    }
}
