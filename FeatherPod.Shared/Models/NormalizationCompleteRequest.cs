namespace FeatherPod.Shared.Models;

/// <summary>
/// Request from the Function to signal normalization completion (success or failure).
/// </summary>
public record NormalizationCompleteRequest
{
    required public bool Success { get; init; }

    public long? NormalizedFileSize { get; init; }

    public long? AudioDurationMs { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// The job attempt the signalling run belonged to (null for the first attempt). A signal for an attempt that is no longer
    /// current is ignored.
    /// </summary>
    public int? Attempt { get; init; }
}
