using System.Security.Cryptography;
using System.Text;

namespace FeatherPod.Models;

public class Episode
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime PublishedDate { get; set; }
    public string Url { get; set; } = string.Empty;

    public static string GenerateId(string fileName, long fileSize)
    {
        var input = $"{fileName}:{fileSize}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
    }

    public string GetAudioUrl(string baseUrl)
    {
        return $"{baseUrl}/audio/{Uri.EscapeDataString(FileName)}";
    }
}
