using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Wraps yt-dlp invocations for YouTube metadata and download operations.
/// </summary>
public partial class YtDlpService
{
    private static readonly Regex VideoUrlRegex = GenerateVideoUrlRegex();
    private static readonly Regex PlaylistParamRegex = GeneratePlaylistParamRegex();
    private static readonly Regex ChannelUrlRegex = GenerateChannelUrlRegex();
    private static readonly Regex ShortsUrlRegex = GenerateShortsUrlRegex();
    private static readonly Regex SearchUrlRegex = GenerateSearchUrlRegex();
    private static readonly Regex ProgressRegex = GenerateProgressRegex();

    private readonly YtDlpBinaryManager _binaryManager;
    private readonly ILogger<YtDlpService>? _logger;

    public YtDlpService(YtDlpBinaryManager binaryManager, ILogger<YtDlpService>? logger = null)
    {
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <summary>
    /// Validates a URL is an acceptable single YouTube video URL.
    /// Returns the video ID if valid, null if not.
    /// </summary>
    public static string? ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Reject playlist URLs
        if (PlaylistParamRegex.IsMatch(url))
        {
            return null;
        }

        // Reject channel URLs
        if (ChannelUrlRegex.IsMatch(url))
        {
            return null;
        }

        // Reject shorts
        if (ShortsUrlRegex.IsMatch(url))
        {
            return null;
        }

        // Reject search URLs
        if (SearchUrlRegex.IsMatch(url))
        {
            return null;
        }

        var match = VideoUrlRegex.Match(url);

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Fetches metadata for a YouTube video without downloading.
    /// </summary>
    public async Task<YtDlpMetadata?> GetMetadataAsync(string url, CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryManager.GetBinaryPath();

        var args = $"--dump-json --no-download --no-playlist \"{url}\"";

        _logger?.LogInformation("Fetching YouTube metadata for {Url}", url);

        var (exitCode, stdout, stderr) = await RunProcessAsync(binaryPath, args, null, cancellationToken);

        if (exitCode != 0)
        {
            _logger?.LogWarning("yt-dlp metadata fetch failed (exit {ExitCode}): {Stderr}", exitCode, stderr);

            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(stdout);

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse yt-dlp metadata JSON");

            return null;
        }
    }

    /// <summary>
    /// Downloads a YouTube video. Returns the output file path on success.
    /// </summary>
    public async Task<string?> DownloadAsync(
        string url,
        string videoId,
        string format,
        string outputDir,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryManager.GetBinaryPath();

        Directory.CreateDirectory(outputDir);

        var outputTemplate = Path.Combine(outputDir, $"{videoId}.%(ext)s");

        var formatArgs = string.Equals(format, "video", StringComparison.OrdinalIgnoreCase)
            ? "-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\" --merge-output-format mp4"
            : "--extract-audio --audio-format m4a --audio-quality 0";

        var args = $"{formatArgs} --no-playlist --no-overwrites --newline -o \"{outputTemplate}\" \"{url}\"";

        _logger?.LogInformation("Downloading YouTube {Format} for {VideoId} to {OutputDir}", format, videoId, outputDir);

        var (exitCode, _, stderr) = await RunProcessAsync(binaryPath, args, line =>
        {
            var match = ProgressRegex.Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
            {
                progressCallback?.Invoke(percent);
            }
        }, cancellationToken);

        if (exitCode != 0)
        {
            _logger?.LogWarning("yt-dlp download failed (exit {ExitCode}): {Stderr}", exitCode, stderr);

            return null;
        }

        // Find the output file (yt-dlp may have changed the extension)
        var expectedExtension = string.Equals(format, "video", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".m4a";
        var expectedPath = Path.Combine(outputDir, $"{videoId}{expectedExtension}");
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        // Fallback: find any file matching the videoId prefix
        var files = Directory.GetFiles(outputDir, $"{videoId}.*");

        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>
    /// Checks if a yt-dlp failure is an extractor error (YouTube-side change).
    /// </summary>
    public static bool IsExtractorError(string stderr)
    {
        return stderr.Contains("ExtractorError", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("unable to extract", StringComparison.OrdinalIgnoreCase) ||
               stderr.Contains("unable to download webpage", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string binaryPath,
        string arguments,
        Action<string>? stdoutLineCallback,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var stdoutBuilder = new System.Text.StringBuilder();
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stdoutBuilder.AppendLine(line);
            stdoutLineCallback?.Invoke(line);
        }

        var stderr = await stderrTask;

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderr);
    }

    [GeneratedRegex(@"(?:youtube\.com/watch\?v=|youtu\.be/|m\.youtube\.com/watch\?v=)([a-zA-Z0-9_-]{11})")]
    private static partial Regex GenerateVideoUrlRegex();

    [GeneratedRegex(@"[?&]list=")]
    private static partial Regex GeneratePlaylistParamRegex();

    [GeneratedRegex(@"youtube\.com/(?:channel/|@|c/)")]
    private static partial Regex GenerateChannelUrlRegex();

    [GeneratedRegex(@"youtube\.com/shorts/")]
    private static partial Regex GenerateShortsUrlRegex();

    [GeneratedRegex(@"youtube\.com/results")]
    private static partial Regex GenerateSearchUrlRegex();

    [GeneratedRegex(@"\[download\]\s+(\d+\.?\d*)%")]
    private static partial Regex GenerateProgressRegex();
}

public record YtDlpMetadata
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uploader")]
    public string? Channel { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("duration")]
    public double Duration { get; init; }

    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; init; }

    [JsonPropertyName("id")]
    public string? VideoId { get; init; }

    /// <summary>
    /// Parses the yt-dlp upload_date (YYYYMMDD) to a DateTime.
    /// </summary>
    public DateTime? GetUploadDateTime()
    {
        if (string.IsNullOrEmpty(UploadDate) || UploadDate.Length != 8)
        {
            return null;
        }

        if (DateTime.TryParseExact(UploadDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
        {
            return date;
        }

        return null;
    }
}
