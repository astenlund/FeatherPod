namespace FeatherPod.Shared.Models;

/// <summary>
/// Progress update callback data for audio normalization.
/// </summary>
public record ProgressUpdate
{
    required public NormalizationStage Stage { get; init; }

    required public int ProgressPercent { get; init; }

    required public string Message { get; init; }

    public TimeSpan? CurrentPosition { get; init; }

    public TimeSpan? TotalDuration { get; init; }
}
