using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FeatherPod.Shared.Models;
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

    public static string GetCanonicalUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

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
    /// Returns (metadata, error). On failure, metadata is null and error contains yt-dlp's stderr.
    /// </summary>
    public async Task<(YtDlpMetadata? Metadata, string? Error)> GetMetadataAsync(string url, string? cookieFilePath = null, CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryManager.GetBinaryPath();

        var cookieArgs = cookieFilePath != null ? $"--cookies \"{cookieFilePath}\" " : "";
        var denoArgs = GetDenoArgs();
        var args = $"{denoArgs}{cookieArgs}--dump-json --no-download --no-playlist \"{url}\"";

        _logger?.LogInformation("Fetching YouTube metadata for {Url}", url);

        var (exitCode, stdout, stderr) = await RunProcessAsync(binaryPath, args, null, cancellationToken);

        if (exitCode != 0)
        {
            _logger?.LogWarning("yt-dlp metadata fetch failed (exit {ExitCode}): {Stderr}", exitCode, stderr);

            // Extract the user-facing error line from yt-dlp stderr (usually "ERROR: ...")
            var errorLine = stderr?.Split('\n')
                .Select(l => l.Trim())
                .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
            var friendlyError = errorLine?.Length > 7 ? errorLine[7..].Trim() : null;

            return (null, friendlyError ?? "Video is unavailable");
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<YtDlpMetadata>(stdout);

            return (metadata, null);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse yt-dlp metadata JSON");

            return (null, "Failed to read video metadata");
        }
    }

    /// <summary>
    /// Downloads a YouTube video. Returns (outputFilePath, stderr).
    /// outputFilePath is null on failure; stderr is available for error classification.
    /// </summary>
    public async Task<(string? OutputPath, string? Stderr)> DownloadAsync(
        string url,
        string videoId,
        YouTubeFormat format,
        string outputDir,
        Action<double>? progressCallback = null,
        string? cookieFilePath = null,
        string? ffmpegDir = null,
        CancellationToken cancellationToken = default)
    {
        var binaryPath = _binaryManager.GetBinaryPath();

        Directory.CreateDirectory(outputDir);

        var outputTemplate = Path.Combine(outputDir, $"{videoId}.%(ext)s");
        var denoArgs = GetDenoArgs();
        var args = BuildDownloadArgs(url, outputTemplate, format, cookieFilePath, denoArgs, ffmpegDir);

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

            return (null, stderr);
        }

        var expectedPath = Path.Combine(outputDir, $"{videoId}{format.GetExtension()}");
        if (File.Exists(expectedPath))
        {
            return (expectedPath, null);
        }

        // Fallback: find any file matching the videoId prefix
        var files = Directory.GetFiles(outputDir, $"{videoId}.*");

        return files.Length > 0 ? (files[0], null) : (null, stderr);
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

    /// <summary>
    /// Checks if a yt-dlp failure is a format availability error (e.g., missing JS runtime
    /// causes yt-dlp to fall back to limited format lists).
    /// </summary>
    public static bool IsFormatUnavailableError(string text)
    {
        return text.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// User-facing error message for bot detection failures. Used as a sentinel value
    /// to derive AuthRequired on JobStatusResponse.
    /// </summary>
    public const string BotDetectionErrorMessage = "YouTube needs browser cookies to continue";

    /// <summary>
    /// Checks if a yt-dlp failure is a bot detection error requiring cookie authentication.
    /// Works on both friendly error strings (from GetMetadataAsync) and raw stderr (from DownloadAsync).
    /// </summary>
    public static bool IsBotDetectionError(string text)
    {
        return text.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildDownloadArgs(string url, string outputTemplate, YouTubeFormat format, string? cookieFilePath, string denoArgs, string? ffmpegDir)
    {
        var formatArgs = format == YouTubeFormat.Video
            ? "-f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]\" --merge-output-format mp4"
            : "--extract-audio --audio-format m4a --audio-quality 0";

        var cookieArgs = cookieFilePath != null ? $"--cookies \"{cookieFilePath}\" " : "";
        var ffmpegArgs = ffmpegDir != null ? $"--ffmpeg-location \"{ffmpegDir}\" " : "";

        return $"{denoArgs}{cookieArgs}{ffmpegArgs}{formatArgs} --no-playlist --no-overwrites --newline -o \"{outputTemplate}\" \"{url}\"";
    }

    private string GetDenoArgs()
    {
        var denoPath = _binaryManager.GetDenoPath();
        if (denoPath == null)
        {
            return "";
        }

        return $"--js-runtimes deno:\"{denoPath}\" ";
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

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var captureStdout = stdoutLineCallback == null;

            var stdoutBuilder = captureStdout ? new System.Text.StringBuilder() : null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
            {
                stdoutBuilder?.AppendLine(line);
                stdoutLineCallback?.Invoke(line);
            }

            var stderr = await stderrTask;
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, stdoutBuilder?.ToString() ?? string.Empty, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
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
