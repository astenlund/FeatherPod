using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using FFMpegCore;
using FFMpegCore.Extensions.Downloader;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Manages FFmpeg binary availability, including auto-download capabilities.
/// </summary>
public class FFmpegBinaryManager
{
    private const string LockBlobName = ".ffmpeg-download-lock";

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LeaseWaitTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<FFmpegBinaryManager>? _logger;
    private readonly BlobContainerClient? _blobContainer;
    private readonly Lock _lock = new();

    private bool? _isAvailable;
    private bool _configured;

    /// <summary>
    /// Creates an FFmpegBinaryManager with optional logging support.
    /// </summary>
    /// <param name="logger">Optional logger for DI scenarios (Server). Pass null for CLI usage.</param>
    /// <param name="blobContainer">Optional blob container for distributed locking (Functions). Pass null for CLI usage.</param>
    public FFmpegBinaryManager(ILogger<FFmpegBinaryManager>? logger = null, BlobContainerClient? blobContainer = null)
    {
        _logger = logger;
        _blobContainer = blobContainer;
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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if FFmpeg is available after this call.</returns>
    public async Task<bool> EnsureFFmpegAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (IsFFmpegAvailable())
        {
            return true;
        }

        _logger?.LogInformation("FFmpeg not found, attempting to download...");

        return await DownloadFFmpegAsync(cancellationToken);
    }

    /// <summary>
    /// Download FFmpeg binaries to the local directory.
    /// Uses blob lease for distributed locking when blob container is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded.</returns>
    public async Task<bool> DownloadFFmpegAsync(CancellationToken cancellationToken = default)
    {
        var binDir = GetBinaryDirectory();
        Directory.CreateDirectory(binDir);

        // Use distributed lock if blob container is available (Azure Functions scenario)
        if (_blobContainer != null)
        {
            return await DownloadWithDistributedLockAsync(binDir, cancellationToken);
        }

        // Direct download for CLI scenario
        return await DownloadFFmpegCoreAsync(binDir);
    }

    private async Task<bool> DownloadWithDistributedLockAsync(string binDir, CancellationToken cancellationToken = default)
    {
        // Fast path: check if binaries already exist before trying to acquire lease
        if (CheckLocalBinaries(binDir))
        {
            _logger?.LogDebug("FFmpeg binaries already present, skipping distributed lock");
            ConfigureFFMpegCore(binDir);
            lock (_lock) { _isAvailable = true; }

            return true;
        }

        var lockBlob = _blobContainer!.GetBlobClient(LockBlobName);

        // Ensure lock blob exists
        if (!await lockBlob.ExistsAsync())
        {
            try
            {
                await lockBlob.UploadAsync(BinaryData.FromString("ffmpeg-download-lock"), overwrite: false);
                _logger?.LogDebug("Created FFmpeg download lock blob");
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Another instance created it, that's fine
                _logger?.LogDebug("Lock blob already exists (created by another instance)");
            }
        }

        var leaseClient = lockBlob.GetBlobLeaseClient();
        var startTime = DateTime.UtcNow;
        var random = new Random();

        while (DateTime.UtcNow - startTime < LeaseWaitTimeout && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Try to acquire lease
                var lease = await leaseClient.AcquireAsync(LeaseDuration);
                _logger?.LogInformation("Acquired FFmpeg download lease: {LeaseId}", lease.Value.LeaseId);

                try
                {
                    // Double-check if binaries appeared while waiting
                    if (CheckLocalBinaries(binDir))
                    {
                        _logger?.LogInformation("FFmpeg binaries found after acquiring lease (another instance completed download)");
                        ConfigureFFMpegCore(binDir);
                        lock (_lock) { _isAvailable = true; }

                        return true;
                    }

                    // Perform the download
                    var result = await DownloadFFmpegCoreAsync(binDir);

                    return result;
                }
                finally
                {
                    try
                    {
                        await leaseClient.ReleaseAsync();
                        _logger?.LogDebug("Released FFmpeg download lease");
                    }
                    catch (RequestFailedException ex)
                    {
                        _logger?.LogWarning(ex, "Failed to release FFmpeg download lease (may have expired)");
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Lease held by another instance
                _logger?.LogInformation("FFmpeg download lease held by another instance, waiting...");

                // Check if binaries appeared while waiting
                if (CheckLocalBinaries(binDir))
                {
                    _logger?.LogInformation("FFmpeg binaries now available (downloaded by another instance)");
                    ConfigureFFMpegCore(binDir);
                    lock (_lock) { _isAvailable = true; }

                    return true;
                }

                // Jitter: 4-6 seconds to avoid thundering herd
                var jitterMs = 4000 + random.Next(2000);
                await Task.Delay(jitterMs, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger?.LogError("Timed out waiting for FFmpeg download lease");

        // Last chance: check if binaries are available
        if (CheckLocalBinaries(binDir))
        {
            _logger?.LogInformation("FFmpeg binaries found after timeout");
            ConfigureFFMpegCore(binDir);
            lock (_lock) { _isAvailable = true; }

            return true;
        }

        return false;
    }

    private async Task<bool> DownloadFFmpegCoreAsync(string binDir)
    {
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
