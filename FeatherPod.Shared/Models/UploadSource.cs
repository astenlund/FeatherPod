namespace FeatherPod.Shared.Models;

/// <summary>
/// Source of an episode upload.
/// </summary>
public enum UploadSource
{
    /// <summary>
    /// Uploaded via CLI tool.
    /// </summary>
    CLI,

    /// <summary>
    /// Uploaded via browser push page.
    /// </summary>
    Browser,

    /// <summary>
    /// Imported from YouTube via yt-dlp.
    /// </summary>
    YouTube
}
