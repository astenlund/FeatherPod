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
    public MediaSource? MediaSource { get; init; }
    required public DateTime UploadedAt { get; init; }
    public string? Note { get; init; }
    public TranscriptStatus? TranscriptStatus { get; init; }

    /// <summary>
    /// ETag of the audio blob at this episode's file name as its last known writer left it (the upload's normalization, a direct
    /// upload, a move or copy, a feed rename). An audio delete on behalf of the episode is conditional on it, so a blob a later
    /// writer replaced is never removed. Null for episodes stored before ownership was recorded: deleting one is unconditional, and
    /// moving one deletes its source audio only while it is the version the move copied.
    /// </summary>
    public string? AudioETag { get; init; }

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

    public string? GetTranscriptUrl(string baseUrl)
    {
        if (TranscriptStatus != Models.TranscriptStatus.Available)
        {
            return null;
        }

        return $"{baseUrl}/{FeedId}/transcripts/{Uri.EscapeDataString(Id)}.vtt";
    }
}
