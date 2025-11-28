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
    public required string JobId { get; init; }

    /// <summary>
    /// The feed this episode belongs to.
    /// </summary>
    public required string FeedId { get; init; }

    /// <summary>
    /// Original filename of the uploaded audio file.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Original file size in bytes (before normalization).
    /// Used for Episode ID generation.
    /// </summary>
    public required long OriginalFileSize { get; init; }

    /// <summary>
    /// Pre-computed Episode ID: SHA256(feedId:fileName:originalFileSize).
    /// Computed in App Service before queueing to ensure consistency.
    /// </summary>
    public required string EpisodeId { get; init; }

    /// <summary>
    /// Resolved episode title (with fallback logic already applied).
    /// </summary>
    public required string Title { get; init; }

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
    public required DateTime PublishedDate { get; init; }

    /// <summary>
    /// Timestamp when the job was queued.
    /// </summary>
    public required DateTime QueuedAt { get; init; }
}
