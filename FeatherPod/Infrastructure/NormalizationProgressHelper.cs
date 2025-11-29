namespace FeatherPod.Infrastructure;

/// <summary>
/// Shared helper for formatting normalization progress display.
/// </summary>
public static class NormalizationProgressHelper
{
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
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"h\:mm\:ss");
        }

        return time.ToString(@"mm\:ss");
    }

    public static string GetStageDescription(string? stage)
    {
        return stage switch
        {
            "Queued" => "Queued",
            "Downloading" => "Downloading",
            "Analyzing" => "Analyzing",
            "Normalizing" => "Normalizing",
            "Uploading" => "Uploading",
            "Finalizing" => "Finalizing",
            "Completed" => "Complete",
            "Failed" => "Failed",
            _ => "Processing"
        };
    }
}
