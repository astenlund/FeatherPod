namespace FeatherPod.Shared.Models;

/// <summary>
/// Represents a YouTube download job processed by the in-process BackgroundService.
/// Metadata is fetched via yt-dlp before queueing.
/// </summary>
public record YouTubeDownloadJob
{
    required public string JobId { get; init; }

    required public string FeedId { get; init; }

    required public string Url { get; init; }

    required public string VideoId { get; init; }

    /// <summary>
    /// "audio" or "video".
    /// </summary>
    required public string Format { get; init; }

    required public string EpisodeId { get; init; }

    required public string Title { get; init; }

    public string? Channel { get; init; }

    public string? Description { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTime? UploadDate { get; init; }

    required public DateTime QueuedAt { get; init; }
}
