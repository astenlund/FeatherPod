using FeatherPod.Shared.Models;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Shared helper for formatting normalization progress display.
/// </summary>
public static class NormalizationProgressHelper
{
    private static readonly int LocalMaxStageNameLength = Enum.GetNames<NormalizationStage>()
        .Where(n => n != nameof(NormalizationStage.Unknown))
        .Max(s => s.Length);

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
    /// Uses server-provided display name and max length when available for forward compatibility.
    /// </summary>
    public static string GetStageDescription(ProgressUpdate update)
    {
        var displayName = update.StageDisplayName
            ?? (update.Stage != NormalizationStage.Unknown ? update.Stage.ToString() : null)
            ?? "Processing";

        var maxLength = update.StageDisplayNameMaxLength ?? LocalMaxStageNameLength;

        return displayName.PadRight(maxLength);
    }
}
