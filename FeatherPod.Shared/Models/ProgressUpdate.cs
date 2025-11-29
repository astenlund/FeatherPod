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

    /// <summary>
    /// Server-provided display name for the stage (for forward compatibility).
    /// </summary>
    public string? StageDisplayName { get; init; }

    /// <summary>
    /// Server-provided max length for stage display names (for UI alignment).
    /// </summary>
    public int? StageDisplayNameMaxLength { get; init; }
}
