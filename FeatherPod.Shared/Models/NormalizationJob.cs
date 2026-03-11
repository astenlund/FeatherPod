namespace FeatherPod.Shared.Models;

/// <summary>
/// Represents a normalization job message sent to the Azure Queue.
/// All metadata is extracted in the App Service before queueing.
/// </summary>
public record NormalizationJob
{
    /// <summary>
    /// Unique job identifier (GUID).
    /// </summary>
    required public string JobId { get; init; }

    /// <summary>
    /// The feed this episode belongs to.
    /// </summary>
    required public string FeedId { get; init; }

    /// <summary>
    /// Original filename of the uploaded audio file.
    /// </summary>
    required public string FileName { get; init; }

    /// <summary>
    /// Original file size in bytes (before normalization).
    /// Used for Episode ID generation.
    /// </summary>
    required public long OriginalFileSize { get; init; }

    /// <summary>
    /// Pre-computed Episode ID: SHA256(feedId:fileName:originalFileSize).
    /// Computed in App Service before queueing to ensure consistency.
    /// </summary>
    required public string EpisodeId { get; init; }

    /// <summary>
    /// Resolved episode title (with fallback logic already applied).
    /// </summary>
    required public string Title { get; init; }

    /// <summary>
    /// Full description for RSS feed (optional).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Short summary for iTunes (optional).
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Published date extracted from original file metadata (before normalization).
    /// </summary>
    required public DateTime PublishedDate { get; init; }

    /// <summary>
    /// Timestamp when the job was queued.
    /// </summary>
    required public DateTime QueuedAt { get; init; }

    /// <summary>
    /// Upload source for tracking (CLI, Browser).
    /// </summary>
    public UploadSource Source { get; init; } = UploadSource.CLI;

    /// <summary>
    /// Progress delivery mode: "poll", "push", or "signalr" (null = poll).
    /// </summary>
    public string? ProgressMode { get; init; }

    /// <summary>
    /// Progress update throttle interval in milliseconds (null = 500).
    /// </summary>
    public int? ProgressIntervalMs { get; init; }
}
