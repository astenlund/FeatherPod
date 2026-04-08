using System.Text.Json;

using Azure.Storage.Blobs;

using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Manages YouTube cookie file storage and yt-dlp integration.
/// Cookies are stored in blob storage and written to temp files for yt-dlp invocations.
/// </summary>
public class YouTubeCookieService
{
    private const string CookieBlobName = "youtube-cookies.txt";
    private const string MetaBlobName = "youtube-cookies-meta.json";

    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<YouTubeCookieService> _logger;
    private bool? _hasCookiesCache;

    public YouTubeCookieService(BlobContainerClient containerClient, ILogger<YouTubeCookieService> logger)
    {
        _containerClient = containerClient;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a cookie file to blob storage after basic format validation.
    /// </summary>
    public async Task UploadCookiesAsync(Stream cookieFile, string userId, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(cookieFile, leaveOpen: true);
        var firstLine = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            throw new ArgumentException("Cookie file is empty");
        }

        // Basic Netscape cookie file validation: first non-empty line should be
        // a comment (# ...) or a domain entry (starts with . or a domain name)
        var trimmed = firstLine.Trim();
        if (!trimmed.StartsWith('#') && !trimmed.StartsWith('.') && !trimmed.Contains('\t'))
        {
            throw new ArgumentException("Invalid cookie file format. Expected a Netscape-format cookies.txt file.");
        }

        // Reset stream and upload
        cookieFile.Position = 0;
        var fileSize = cookieFile.Length;

        var cookieBlob = _containerClient.GetBlobClient(CookieBlobName);
        await cookieBlob.UploadAsync(cookieFile, overwrite: true, cancellationToken);

        // Save metadata
        var meta = new YouTubeCookieInfo
        {
            UploadedAt = DateTimeOffset.UtcNow,
            UploadedBy = userId,
            FileSize = fileSize
        };

        var metaJson = JsonSerializer.Serialize(meta);
        using var metaStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metaJson));
        var metaBlob = _containerClient.GetBlobClient(MetaBlobName);
        await metaBlob.UploadAsync(metaStream, overwrite: true, cancellationToken);

        _hasCookiesCache = true;
        _logger.LogInformation("YouTube cookies uploaded by {UserId} ({Size} bytes)", userId, fileSize);
    }

    /// <summary>
    /// Downloads cookies from blob storage to a temp file in the given directory.
    /// Returns the file path for yt-dlp's --cookies flag, or null if no cookies are stored.
    /// </summary>
    public async Task<string?> GetCookieFilePathAsync(string tempDir, CancellationToken cancellationToken = default)
    {
        var cookieBlob = _containerClient.GetBlobClient(CookieBlobName);
        var exists = await cookieBlob.ExistsAsync(cancellationToken);

        if (!exists.Value)
        {
            return null;
        }

        Directory.CreateDirectory(tempDir);
        var cookiePath = Path.Combine(tempDir, "cookies.txt");
        await cookieBlob.DownloadToAsync(cookiePath, cancellationToken);

        return cookiePath;
    }

    /// <summary>
    /// Checks if cookies exist in blob storage. Result is cached after first check
    /// and invalidated on upload/delete to avoid blob round-trips on health probes.
    /// </summary>
    public async Task<bool> HasCookiesAsync(CancellationToken cancellationToken = default)
    {
        if (_hasCookiesCache.HasValue)
        {
            return _hasCookiesCache.Value;
        }

        var cookieBlob = _containerClient.GetBlobClient(CookieBlobName);
        var exists = await cookieBlob.ExistsAsync(cancellationToken);
        _hasCookiesCache = exists.Value;

        return exists.Value;
    }

    /// <summary>
    /// Deletes stored cookies and metadata.
    /// </summary>
    public async Task DeleteCookiesAsync(CancellationToken cancellationToken = default)
    {
        var cookieBlob = _containerClient.GetBlobClient(CookieBlobName);
        await cookieBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        var metaBlob = _containerClient.GetBlobClient(MetaBlobName);
        await metaBlob.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        _hasCookiesCache = false;
        _logger.LogInformation("YouTube cookies deleted");
    }

    /// <summary>
    /// Returns metadata about the stored cookies, or null if none exist.
    /// </summary>
    public async Task<YouTubeCookieInfo?> GetCookieInfoAsync(CancellationToken cancellationToken = default)
    {
        var metaBlob = _containerClient.GetBlobClient(MetaBlobName);
        var exists = await metaBlob.ExistsAsync(cancellationToken);

        if (!exists.Value)
        {
            return null;
        }

        var response = await metaBlob.DownloadContentAsync(cancellationToken);

        return JsonSerializer.Deserialize<YouTubeCookieInfo>(response.Value.Content.ToString());
    }
}
