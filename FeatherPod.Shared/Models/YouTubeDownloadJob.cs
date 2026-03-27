namespace FeatherPod.Shared.Models;

public enum YouTubeFormat
{
    Audio,
    Video
}

public static class YouTubeFormatExtensions
{
    public static string GetExtension(this YouTubeFormat format) => format switch
    {
        YouTubeFormat.Video => ".mp4",
        _ => ".m4a"
    };
}

/// <summary>
/// Represents a YouTube download job processed by the in-process BackgroundService.
/// Metadata is fetched via yt-dlp before queueing.
/// </summary>
public record YouTubeDownloadJob
{
    required public string JobId { get; init; }

    required public string FeedId { get; init; }

    required public string VideoId { get; init; }

    required public YouTubeFormat Format { get; init; }

    required public string EpisodeId { get; init; }

    required public string Title { get; init; }

    public string? Channel { get; init; }

    public string? Description { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTime? UploadDate { get; init; }

    required public DateTime QueuedAt { get; init; }
}
