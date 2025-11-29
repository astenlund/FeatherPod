namespace FeatherPod.Shared.Models;

/// <summary>
/// Stages of async audio normalization processing.
/// </summary>
public enum NormalizationStage
{
    Unknown = 0,
    Queued,
    Downloading,
    Analyzing,
    Normalizing,
    Uploading,
    Finalizing,
    Completed,
    Failed
}
