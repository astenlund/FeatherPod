namespace FeatherPod.Shared.Models;

/// <summary>
/// Phase of the normalization process.
/// </summary>
public enum NormalizationPhase
{
    /// <summary>
    /// Pass 1: Analyze loudness levels.
    /// </summary>
    Analyze,

    /// <summary>
    /// Pass 2: Apply normalization with measured values.
    /// </summary>
    Normalize
}

/// <summary>
/// Represents a normalization job message sent to the Azure Queue.
/// All metadata is extracted in the App Service before queueing.
/// Jobs are split into two phases (Analyze and Normalize) to stay within
/// Azure Functions Consumption plan timeout limits.
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

    /// <summary>
    /// Current phase of the normalization process.
    /// Defaults to Analyze for new jobs.
    /// </summary>
    public NormalizationPhase Phase { get; init; } = NormalizationPhase.Analyze;

    /// <summary>
    /// Audio duration in milliseconds. Set after analysis phase.
    /// </summary>
    public long? TotalDurationMs { get; init; }

    /// <summary>
    /// Loudness analysis results from Pass 1. Set when Phase=Normalize.
    /// </summary>
    public LoudnessAnalysisData? Analysis { get; init; }
}

/// <summary>
/// Loudness analysis data passed between Analyze and Normalize phases.
/// </summary>
public record LoudnessAnalysisData
{
    public required string InputI { get; init; }
    public required string InputTp { get; init; }
    public required string InputLra { get; init; }
    public required string InputThresh { get; init; }
    public required string TargetOffset { get; init; }

    /// <summary>
    /// Create from FFmpeg LoudnessAnalysis result.
    /// </summary>
    public static LoudnessAnalysisData FromAnalysis(LoudnessAnalysis analysis) => new()
    {
        InputI = analysis.InputI,
        InputTp = analysis.InputTp,
        InputLra = analysis.InputLra,
        InputThresh = analysis.InputThresh,
        TargetOffset = analysis.TargetOffset
    };

    /// <summary>
    /// Convert to LoudnessAnalysis for use with normalization service.
    /// </summary>
    public LoudnessAnalysis ToLoudnessAnalysis() => new()
    {
        InputI = InputI,
        InputTp = InputTp,
        InputLra = InputLra,
        InputThresh = InputThresh,
        TargetOffset = TargetOffset
    };
}
