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

    /// <summary>
    /// Transcribing audio via Whisper STT.
    /// Explicit value to leave gap after Downloading = 10.
    /// Completed/Failed/Cancelled auto-assign after this (16/17/18).
    /// </summary>
    Transcribing = 15,

    Completed,
    Failed,
    Cancelled
}
