namespace FeatherPod.Shared.Models;

/// <summary>
/// Stages of async audio normalization processing.
/// </summary>
public enum NormalizationStage
{
    Unknown = 0,
    Queued,
    Preparing,
    Analyzing,
    Normalizing,
    Finishing,

    /// <summary>
    /// Downloading media from external source (e.g., YouTube via yt-dlp).
    /// Explicit value to avoid shifting existing ordinals.
    /// </summary>
    Downloading = 10,

    Completed,
    Failed,
    Cancelled
}
