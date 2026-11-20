using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// Thread-safe in-memory <see cref="IJobService"/> for unit tests. Mutations from
/// background services are safe; tests can also seed entities directly via
/// <see cref="SetEntity"/> and observe state via <see cref="GetEntity"/> (the stored rows themselves). Like Table Storage, the
/// <see cref="IJobService"/> reads and write results return detached copies, so a caller's change to one reaches the store only
/// through a write.
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
    /// are guaranteed to have completed. The wait registers synchronously, before the method's first
    /// await, and a call that happens before it is not observed, so start the wait before triggering
    /// the work and await the returned task afterwards.
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

    /// <summary>
    /// Normalization messages queued so far, in order.
    /// </summary>
    public List<NormalizationJob> QueuedMessages { get; } = [];

    /// <summary>
    /// When set, <see cref="QueueNormalizationJobAsync"/> throws it (simulates an unavailable queue).
    /// </summary>
    public Exception? QueueThrows { get; set; }

    public Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default)
    {
        if (QueueThrows is not null)
        {
            throw QueueThrows;
        }

        lock (_lock)
        {
            QueuedMessages.Add(job);
        }

        return Task.CompletedTask;
    }

    public Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? signal;
        JobStatusEntity? entity;
        lock (_lock)
        {
            // Copied under the lock the in-place writers hold, so a read never sees half of a write
            entity = Detach(_entities.GetValueOrDefault(jobId));
            _processingCompleteSignals.Remove(jobId, out signal);
        }

        signal?.TrySetResult();

        return Task.FromResult(entity);
    }

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>
    /// A copy of a stored row, as a Table Storage read returns a detached entity: a caller's changes to it reach the store only through a
    /// write. <see cref="JobStatusEntity"/> holds only scalar and string properties, so a member-wise copy is enough.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(entity))]
    private static JobStatusEntity? Detach(JobStatusEntity? entity) => entity == null ? null : (JobStatusEntity)MemberwiseCloneMethod.Invoke(entity, null)!;

    /// <summary>
    /// Stores the queued entity <see cref="JobStatusEntity.CreateQueued"/> builds, keeping an existing one as JobService does.
    /// </summary>
    public Task CreateJobStatusAsync(CreateJobOptions options, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _entities.TryAdd(options.JobId, JobStatusEntity.CreateQueued(options));
        }

        return Task.CompletedTask;
    }

    public Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            List<JobStatusEntity> matches = [.. _entities.Values.Where(e => e.FeedId == feedId && !e.GetJobStatus().IsTerminal()).Select(e => Detach(e))];

            return Task.FromResult(matches);
        }
    }

    public Task<List<JobStatusEntity>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            List<JobStatusEntity> matches = [.. _entities.Values.Where(e => !e.GetJobStatus().IsTerminal()).Select(e => Detach(e))];

            return Task.FromResult(matches);
        }
    }

    /// <summary>
    /// When set, <see cref="TryReplaceAsync"/> refuses the replace as a concurrent change would (simulates a lost ETag race).
    /// </summary>
    public bool RefuseNextReplace { get; set; }

    public Task<bool> TryReplaceAsync(JobStatusEntity entity, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (RefuseNextReplace || !_entities.ContainsKey(entity.RowKey))
            {
                RefuseNextReplace = false;

                return Task.FromResult(false);
            }

            _entities[entity.RowKey] = Detach(entity);

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// When set, <see cref="GetActiveJobsByTranscriptionStatusAsync"/> throws it (simulates an unavailable table).
    /// </summary>
    public Exception? TranscriptionStatusQueryThrows { get; set; }

    public Task<List<JobStatusEntity>> GetActiveJobsByTranscriptionStatusAsync(IReadOnlyCollection<string> transcriptionStatuses, CancellationToken cancellationToken = default)
    {
        if (TranscriptionStatusQueryThrows is not null)
        {
            throw TranscriptionStatusQueryThrows;
        }

        lock (_lock)
        {
            List<JobStatusEntity> matches = [.. _entities.Values.Where(e => e.TranscriptionStatus is not null && transcriptionStatuses.Contains(e.TranscriptionStatus) && !e.GetJobStatus().IsTerminal()).Select(e => Detach(e))];

            return Task.FromResult(matches);
        }
    }

    public Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - since;
        lock (_lock)
        {
            List<JobStatusEntity> matches = [.. _entities.Values.Where(e => e.FeedId == feedId && e.QueuedAt >= cutoff).Select(e => Detach(e))];

            return Task.FromResult(matches);
        }
    }

    public Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default, int? expectedAttempt = null) => Task.FromResult<JobStatusEntity?>(null);

    public Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            mutate(entity);

            return Task.FromResult<JobStatusEntity?>(Detach(entity));
        }
    }

    public Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default, int? expectedAttempt = null, Func<JobStatusEntity, bool>? onlyIf = null)
    {
        lock (_lock)
        {
            if (!_entities.TryGetValue(jobId, out var entity))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            // Mirrors JobService: a terminal job, work of an earlier attempt, and a write its predicate rejects write nothing
            if (entity.GetJobStatus().IsTerminal() || (expectedAttempt is { } expected && expected != entity.GetAttempt()) || (onlyIf != null && !onlyIf(entity)))
            {
                return Task.FromResult<JobStatusEntity?>(null);
            }

            configure(entity);

            return Task.FromResult<JobStatusEntity?>(Detach(entity));
        }
    }

    /// <summary>
    /// When set, the next <see cref="MergeWithETagAsync"/> runs this callback (outside the lock, so it may change entities) and then
    /// fails with 412 as a concurrent change would, without merging; the hook clears itself first.
    /// </summary>
    public Action? BeforeNextETagMergeConflict { get; set; }

    public Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default)
    {
        if (BeforeNextETagMergeConflict is { } conflict)
        {
            BeforeNextETagMergeConflict = null;
            conflict();

            throw new Azure.RequestFailedException(412, "Precondition failed");
        }

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
