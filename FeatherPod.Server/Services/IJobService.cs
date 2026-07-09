using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Service for managing normalization jobs (queue and status tracking).
/// </summary>
public interface IJobService
{
    /// <summary>
    /// Initialize queue and table storage (create if not exists).
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Queue a normalization job for async processing.
    /// </summary>
    Task QueueNormalizationJobAsync(NormalizationJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the status of a job by ID.
    /// </summary>
    Task<JobStatusEntity?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create initial job status entry (Queued state).
    /// </summary>
    Task CreateJobStatusAsync(CreateJobOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active (non-terminal) jobs for a feed.
    /// </summary>
    Task<List<JobStatusEntity>> GetActiveJobsByFeedAsync(string feedId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all jobs (including terminal) for a feed within a time window.
    /// </summary>
    Task<List<JobStatusEntity>> GetRecentJobsByFeedAsync(string feedId, TimeSpan since, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job. Returns updated entity, or null if the job is already in a terminal state.
    /// </summary>
    Task<JobStatusEntity?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update a job's status fields atomically (read-modify-write with ETag).
    /// The <paramref name="mutate"/> action modifies the entity in place.
    /// Returns the updated entity, or null if the job was not found.
    /// </summary>
    Task<JobStatusEntity?> UpdateJobStatusAsync(string jobId, Action<JobStatusEntity> mutate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merge specific fields into a job entity without stomping concurrent writes.
    /// The <paramref name="configure"/> action populates a blank entity with only the fields to write.
    /// Reads the entity first for terminal-state guard and ETag, then Merges the partial entity.
    /// Returns the merged view (read entity with partial applied), or null if not found or already terminal.
    /// </summary>
    Task<JobStatusEntity?> MergeJobFieldsAsync(string jobId, Action<JobStatusEntity> configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merge specific fields using a caller-provided ETag (no re-read).
    /// Used by CAS guards that need the ETag from a prior read.
    /// Throws RequestFailedException(412) on conflict -- caller must handle.
    /// </summary>
    Task MergeWithETagAsync(string jobId, Action<JobStatusEntity> configure, Azure.ETag etag, CancellationToken cancellationToken = default);
}
