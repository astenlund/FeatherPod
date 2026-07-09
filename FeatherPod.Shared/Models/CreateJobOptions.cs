namespace FeatherPod.Shared.Models;

/// <summary>
/// Parameters for creating a queued job status entry.
/// Consolidates the fields carried from upload into <see cref="JobStatusEntity.CreateQueued"/>.
/// </summary>
public record CreateJobOptions
{
    /// <summary>
    /// The job ID (row key).
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// The feed ID this job belongs to.
    /// </summary>
    public required string FeedId { get; init; }

    /// <summary>
    /// Original file name of the uploaded audio file.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Episode title (AI-generated or from YouTube metadata).
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Progress delivery mode: "poll", "push", or "signalr" (null = poll).
    /// </summary>
    public string? ProgressMode { get; init; }

    /// <summary>
    /// Progress update throttle interval in milliseconds (null = 500).
    /// </summary>
    public int? ProgressIntervalMs { get; init; }

    /// <summary>
    /// Episode description (carried from upload for join-time episode creation).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Episode summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Episode publish date.
    /// </summary>
    public DateTimeOffset? PublishedDate { get; init; }

    /// <summary>
    /// Upload source (Browser, CLI).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Pre-normalization file size (for episode ID verification).
    /// </summary>
    public long? OriginalFileSize { get; init; }

    /// <summary>
    /// The Episode ID once known (preserves play progress on re-upload).
    /// </summary>
    public string? EpisodeId { get; init; }

    /// <summary>
    /// Transcription track status: null (disabled), Queued, Running, Completed, Failed.
    /// </summary>
    public string? TranscriptionStatus { get; init; }
}
