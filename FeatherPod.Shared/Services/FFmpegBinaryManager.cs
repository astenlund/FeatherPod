using FFMpegCore;
using FFMpegCore.Extensions.Downloader;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Manages FFmpeg binary availability, including auto-download capabilities.
/// </summary>
public class FFmpegBinaryManager
{
    private readonly ILogger<FFmpegBinaryManager>? _logger;
    private readonly Lock _lock = new();

    private bool? _isAvailable;
    private bool _configured;

    /// <summary>
    /// Creates an FFmpegBinaryManager with optional logging support.
    /// </summary>
    /// <param name="logger">Optional logger for DI scenarios (Server). Pass null for CLI usage.</param>
    public FFmpegBinaryManager(ILogger<FFmpegBinaryManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the platform-specific directory for storing downloaded FFmpeg binaries.
    /// </summary>
    public static string GetBinaryDirectory()
    {
        // Azure App Service/Functions: Use HOME directory for persistent storage
        var websiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (websiteName != null)
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                // Windows: D:\home, Linux: /home
                return Path.Combine(home, ".featherpod", "ffmpeg");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(localAppData, "FeatherPod", "ffmpeg");
        }

        // Linux/macOS: Use XDG Base Directory spec ($XDG_DATA_HOME or ~/.local/share)
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "FeatherPod", "ffmpeg");
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(userHome, ".local", "share", "FeatherPod", "ffmpeg");
    }

    /// <summary>
    /// Check if FFmpeg is available (either on PATH or in local download directory).
    /// </summary>
    public bool IsFFmpegAvailable()
    {
        if (_isAvailable.HasValue)
        {
            return _isAvailable.Value;
        }

        lock (_lock)
        {
            if (_isAvailable.HasValue)
            {
                return _isAvailable.Value;
            }

            // First, check system PATH
            if (CheckSystemPath())
            {
                _logger?.LogInformation("FFmpeg found on system PATH");
                _isAvailable = true;

                return true;
            }

            // Then check local download directory
            var binDir = GetBinaryDirectory();
            if (CheckLocalBinaries(binDir))
            {
                ConfigureFFMpegCore(binDir);
                _logger?.LogInformation("FFmpeg found in local directory: {BinDir}", binDir);
                _isAvailable = true;

                return true;
            }

            _logger?.LogWarning("FFmpeg not available on PATH or in local directory");
            _isAvailable = false;

            return false;
        }
    }

    /// <summary>
    /// Ensures FFmpeg is available, downloading if necessary.
    /// </summary>
    /// <returns>True if FFmpeg is available after this call</returns>
    public async Task<bool> EnsureFFmpegAvailableAsync()
    {
        if (IsFFmpegAvailable())
        {
            return true;
        }

        _logger?.LogInformation("FFmpeg not found, attempting to download...");
        return await DownloadFFmpegAsync();
    }

    /// <summary>
    /// Download FFmpeg binaries to the local directory.
    /// </summary>
    /// <returns>True if download succeeded</returns>
    public async Task<bool> DownloadFFmpegAsync()
    {
        var binDir = GetBinaryDirectory();

        Directory.CreateDirectory(binDir);

        _logger?.LogInformation("Downloading FFmpeg binaries to {BinDir}...", binDir);

        try
        {
            var options = new FFOptions { BinaryFolder = binDir };
            var downloadedFiles = await FFMpegDownloader.DownloadBinaries(options: options);

            if (downloadedFiles.Count == 0)
            {
                _logger?.LogError("FFmpeg download returned no files");
                return false;
            }

            _logger?.LogInformation("Downloaded {Count} FFmpeg binaries: {Files}", downloadedFiles.Count, string.Join(", ", downloadedFiles));
            ConfigureFFMpegCore(binDir);

            if (!CheckLocalBinaries(binDir))
            {
                _logger?.LogError("FFmpeg binaries not found after download");

                return false;
            }

            lock (_lock)
            {
                _isAvailable = true;
            }

            _logger?.LogInformation("FFmpeg download complete and verified");

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download FFmpeg binaries");

            return false;
        }
    }

    private static bool CheckSystemPath()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckLocalBinaries(string binDir)
    {
        if (!Directory.Exists(binDir))
        {
            return false;
        }

        var ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        var ffmpegPath = Path.Combine(binDir, ffmpegName);
        var ffprobePath = Path.Combine(binDir, ffprobeName);

        return File.Exists(ffmpegPath) && File.Exists(ffprobePath);
    }

    private void ConfigureFFMpegCore(string binDir)
    {
        if (_configured)
        {
            return;
        }

        lock (_lock)
        {
            if (_configured)
            {
                return;
            }

            GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = binDir;
            });

            _configured = true;

            _logger?.LogDebug("Configured FFMpegCore to use binaries from {BinDir}", binDir);
        }
    }
}
