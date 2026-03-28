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
/// Contains only enqueue-time data; full metadata is fetched by the background worker during the Preparing stage.
/// </summary>
public record YouTubeDownloadJob
{
    required public string JobId { get; init; }

    required public string FeedId { get; init; }

    required public string VideoId { get; init; }

    required public YouTubeFormat Format { get; init; }

    required public string EpisodeId { get; init; }

    /// <summary>
    /// Display title from oEmbed (or videoId fallback). The authoritative yt-dlp title is fetched by the background worker.
    /// </summary>
    required public string Title { get; init; }

    required public DateTime QueuedAt { get; init; }
}
