using FeatherPod.Shared.Services;

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
    /// Current status: Queued, Processing, Completed, Failed, or Cancelled.
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
    public double? ProgressPercent { get; init; }

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
    /// Original file name of the uploaded audio file.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Episode title (AI-generated or from YouTube metadata).
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Whether normalization has completed (success or failure).
    /// </summary>
    public bool NormalizationComplete { get; init; }

    /// <summary>
    /// Error message if normalization failed.
    /// </summary>
    public string? NormalizationError { get; init; }

    /// <summary>
    /// Transcription track status: null (disabled), Queued, Running, Completed, Failed.
    /// </summary>
    public string? TranscriptionStatus { get; init; }

    /// <summary>
    /// Error message if transcription failed.
    /// </summary>
    public string? TranscriptionError { get; init; }

    /// <summary>
    /// Whether this failure requires YouTube cookie authentication.
    /// Derived from the error message sentinel string.
    /// </summary>
    public bool AuthRequired { get; init; }

    /// <summary>
    /// Milliseconds elapsed since the job was queued, measured server-side.
    /// Used by the client for velocity calculations immune to tab suspension.
    /// </summary>
    public long? TickMs { get; init; }

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
            Status = entity.Status ?? nameof(JobStatus.Queued),
            EpisodeId = entity.EpisodeId,
            FeedId = entity.FeedId,
            Error = entity.Error,
            QueuedAt = entity.QueuedAt,
            CompletedAt = entity.CompletedAt,
            Stage = entity.NormalizationStage,
            StageDisplayName = entity.NormalizationStage,
            StageDisplayNameMaxLength = MaxStageDisplayNameLength,
            ProgressPercent = entity.NormalizationProgress,
            ProgressMessage = entity.ProgressMessage,
            CurrentPositionMs = entity.CurrentPositionMs,
            TotalDurationMs = entity.TotalDurationMs,
            FileName = entity.FileName,
            Title = entity.Title,
            NormalizationComplete = entity.NormalizationComplete ?? false,
            NormalizationError = entity.NormalizationError,
            TranscriptionStatus = entity.TranscriptionStatus,
            TranscriptionError = entity.TranscriptionError,
            AuthRequired = string.Equals(entity.Error, YtDlpService.BotDetectionErrorMessage, StringComparison.Ordinal),
            TickMs = entity.QueuedAt.HasValue ? (long)(DateTimeOffset.UtcNow - entity.QueuedAt.Value).TotalMilliseconds : null
        };
    }
}
