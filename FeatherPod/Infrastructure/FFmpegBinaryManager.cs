using FFMpegCore;
using FFMpegCore.Extensions.Downloader;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Manages FFmpeg binary availability, including auto-download capabilities.
/// </summary>
internal static class FFmpegBinaryManager
{
    private static bool? _isAvailable;
    private static bool _configured;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Gets the platform-specific directory for storing downloaded FFmpeg binaries.
    /// </summary>
    public static string GetBinaryDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "FeatherPod", "ffmpeg");
        }

        // Linux/macOS: XDG standard
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "FeatherPod", "ffmpeg");
    }

    /// <summary>
    /// Check if FFmpeg is available (either on PATH or in local download directory).
    /// </summary>
    public static bool IsFFmpegAvailable()
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
                _isAvailable = true;

                return true;
            }

            // Then check local download directory
            var binDir = GetBinaryDirectory();
            if (CheckLocalBinaries(binDir))
            {
                ConfigureFFMpegCore(binDir);
                _isAvailable = true;

                return true;
            }

            _isAvailable = false;

            return false;
        }
    }

    /// <summary>
    /// Download FFmpeg binaries to the local directory.
    /// </summary>
    /// <returns>True if download succeeded</returns>
    public static async Task<bool> DownloadFFmpegAsync()
    {
        var binDir = GetBinaryDirectory();

        Directory.CreateDirectory(binDir);

        try
        {
            // Create FFOptions with the target binary folder
            var options = new FFOptions { BinaryFolder = binDir };

            // Download FFmpeg and FFprobe binaries
            var downloadedFiles = await FFMpegDownloader.DownloadBinaries(options: options);

            if (downloadedFiles.Count == 0)
            {
                return false;
            }

            // Configure FFMpegCore to use the downloaded binaries
            ConfigureFFMpegCore(binDir);

            // Verify the binaries exist
            if (!CheckLocalBinaries(binDir))
            {
                return false;
            }

            // Update cached state
            lock (_lock)
            {
                _isAvailable = true;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CheckSystemPath()
    {
        try
        {
            // Try to run ffmpeg -version to see if it's on PATH
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
            return false;

        var ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

        var ffmpegPath = Path.Combine(binDir, ffmpegName);
        var ffprobePath = Path.Combine(binDir, ffprobeName);

        return File.Exists(ffmpegPath) && File.Exists(ffprobePath);
    }

    private static void ConfigureFFMpegCore(string binDir)
    {
        if (_configured) return;

        lock (_lock)
        {
            if (_configured) return;

            GlobalFFOptions.Configure(options =>
            {
                options.BinaryFolder = binDir;
            });

            _configured = true;
        }
    }
}
