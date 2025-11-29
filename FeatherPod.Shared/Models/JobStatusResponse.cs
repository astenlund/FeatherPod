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
    /// Current processing stage.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// Display name for the current stage (for UI rendering).
    /// </summary>
    public string? StageDisplayName { get; init; }

    /// <summary>
    /// Maximum length of any stage display name (for UI padding/alignment).
    /// </summary>
    public int? StageDisplayNameMaxLength { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int? ProgressPercent { get; init; }

    /// <summary>
    /// Detailed progress message.
    /// </summary>
    public string? ProgressMessage { get; init; }

    /// <summary>
    /// Current audio position in milliseconds.
    /// </summary>
    public long? CurrentPositionMs { get; init; }

    /// <summary>
    /// Total audio duration in milliseconds.
    /// </summary>
    public long? TotalDurationMs { get; init; }

    /// <summary>
    /// Time taken to process the job, or elapsed time if still processing.
    /// </summary>
    public TimeSpan? Duration => QueuedAt.HasValue
        ? (CompletedAt ?? DateTimeOffset.UtcNow) - QueuedAt.Value
        : null;

    // Cache the max stage name length (excludes Unknown)
    private static readonly int MaxStageDisplayNameLength = Enum.GetNames<NormalizationStage>()
        .Where(n => n != nameof(NormalizationStage.Unknown))
        .Max(n => n.Length);

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
            CompletedAt = entity.CompletedAt,
            Stage = entity.Stage,
            StageDisplayName = entity.Stage,
            StageDisplayNameMaxLength = MaxStageDisplayNameLength,
            ProgressPercent = entity.ProgressPercent,
            ProgressMessage = entity.ProgressMessage,
            CurrentPositionMs = entity.CurrentPositionMs,
            TotalDurationMs = entity.TotalDurationMs
        };
    }
}
