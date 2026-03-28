namespace FeatherPod.Shared.Models;

/// <summary>
/// Origin of an episode's media content.
/// Distinct from <see cref="UploadSource"/> which tracks the access path (Browser/CLI).
/// </summary>
public enum MediaSource
{
    /// <summary>
    /// Imported from YouTube via yt-dlp.
    /// </summary>
    YouTube
}
