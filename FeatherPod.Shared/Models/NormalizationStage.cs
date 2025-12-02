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
    Completed,
    Failed
}
