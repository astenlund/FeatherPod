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
    Task CreateJobStatusAsync(string jobId, string feedId, CancellationToken cancellationToken = default);
}
