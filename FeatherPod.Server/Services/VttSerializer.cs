using System.Text;

namespace FeatherPod.Server.Services;

/// <summary>
/// Serializes diarized speech segments to WebVTT with per-speaker voice tags.
/// Pure data transformation, no I/O.
/// </summary>
public static class VttSerializer
{
    /// <summary>
    /// Serialize diarized segments to VTT with speaker voice tags.
    /// </summary>
    public static string Serialize(IReadOnlyList<DiarizedSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var segment in segments)
        {
            var start = TimeSpan.FromTicks(segment.OffsetTicks);
            var end = start + TimeSpan.FromTicks(segment.DurationTicks);

            sb.AppendLine($"{FormatTimestamp(start)} --> {FormatTimestamp(end)}");
            sb.AppendLine($"<v {segment.SpeakerId}>{segment.Text}</v>");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a <see cref="TimeSpan"/> as a WebVTT timestamp (<c>HH:MM:SS.fff</c>).
    /// </summary>
    internal static string FormatTimestamp(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
