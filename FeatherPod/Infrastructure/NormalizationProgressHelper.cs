using FeatherPod.Shared.Models;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Shared helper for formatting normalization progress display.
/// </summary>
public static class NormalizationProgressHelper
{
    private static readonly int MaxStageNameLength = Enum.GetNames<NormalizationStage>().Max(s => s.Length);

    public static string FormatPosition(TimeSpan? current, TimeSpan? total)
    {
        if (current == null || total == null || total.Value.TotalSeconds <= 0)
        {
            return string.Empty;
        }

        return $"{FormatTime(current.Value)} / {FormatTime(total.Value)}";
    }

    public static string FormatTime(TimeSpan time)
    {
        return time.ToString(time.TotalHours >= 1
            ? @"h\:mm\:ss"
            : @"mm\:ss");
    }

    /// <summary>
    /// Get a fixed-width stage description for consistent progress bar alignment.
    /// </summary>
    public static string GetStageDescription(string? stage)
    {
        var isValidStage = Enum.TryParse<NormalizationStage>(stage, out _);

        return (isValidStage ? stage! : "Processing").PadRight(MaxStageNameLength);
    }
}
