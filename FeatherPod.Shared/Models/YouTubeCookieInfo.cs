namespace FeatherPod.Shared.Models;

/// <summary>
/// Metadata about the uploaded YouTube cookies file.
/// Stored alongside the cookie file in blob storage.
/// </summary>
public record YouTubeCookieInfo
{
    required public DateTimeOffset UploadedAt { get; init; }
    required public string UploadedBy { get; init; }
    required public long FileSize { get; init; }
}
