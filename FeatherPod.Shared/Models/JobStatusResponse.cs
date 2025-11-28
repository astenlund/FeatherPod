namespace FeatherPod.Shared.Models;

/// <summary>
/// API response for job status queries.
/// </summary>
public record JobStatusResponse
{
    /// <summary>
    /// The job ID.
    /// </summary>
    required public string JobId { get; init; }

    /// <summary>
    /// Current status: Queued, Processing, Completed, or Failed.
    /// </summary>
    required public string Status { get; init; }

    /// <summary>
    /// The Episode ID (only present when Completed).
    /// </summary>
    public string? EpisodeId { get; init; }

    /// <summary>
    /// The Feed ID.
    /// </summary>
    public string? FeedId { get; init; }

    /// <summary>
    /// Error message (only present when Failed).
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// When the job was queued.
    /// </summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>
    /// When the job completed or failed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Create from a JobStatusEntity.
    /// </summary>
    public static JobStatusResponse FromEntity(JobStatusEntity entity)
    {
        return new()
        {
            JobId = entity.RowKey,
            Status = entity.Status,
            EpisodeId = entity.EpisodeId,
            FeedId = entity.FeedId,
            Error = entity.Error,
            QueuedAt = entity.QueuedAt,
            CompletedAt = entity.CompletedAt
        };
    }
}
