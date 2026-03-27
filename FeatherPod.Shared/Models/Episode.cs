using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Shared.Models;

public record Episode
{
    required public string Id { get; init; }
    required public string FeedId { get; init; }
    required public string Title { get; init; }
    public string? Description { get; init; }
    public string? Summary { get; init; }
    required public string FileName { get; init; }
    required public long FileSize { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime PublishedDate { get; init; }
    public string? Url { get; init; }
    required public UploadSource Source { get; init; }
    required public DateTime UploadedAt { get; init; }
    public string? Note { get; init; }

    public static string GenerateId(string feedId, string fileName, long fileSize)
    {
        var input = $"{feedId}:{fileName}:{fileSize}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    public static string GenerateYouTubeId(string feedId, string videoId, string format)
    {
        var input = $"{feedId}:{videoId}:{format}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    public string GetAudioUrl(string baseUrl)
    {
        return $"{baseUrl}/{FeedId}/audio/{Uri.EscapeDataString(FileName)}";
    }
}
