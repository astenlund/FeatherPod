using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Manages the yt-dlp binary lifecycle: download, version tracking, and updates.
/// Mirrors <see cref="FFmpegBinaryManager"/> patterns but is simpler (no distributed lock needed).
/// </summary>
public class YtDlpBinaryManager
{
    private const string GitHubReleasesUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    private const string DenoReleasesUrl = "https://api.github.com/repos/denoland/deno/releases/latest";
    private const string VersionFileName = "version.txt";
    private const string DenoVersionFileName = "deno-version.txt";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly ILogger<YtDlpBinaryManager>? _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly string _binaryDirectory;
    private readonly string _binaryPath;
    private readonly string _denoPath;

    private bool? _isAvailable;
    private DateTime _lastUpdateCheck = DateTime.MinValue;

    public YtDlpBinaryManager(ILogger<YtDlpBinaryManager>? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _binaryDirectory = ResolveBinaryDirectory();
        _binaryPath = Path.Combine(_binaryDirectory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        _denoPath = Path.Combine(_binaryDirectory, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
    }

    public static string GetBinaryDirectory() => ResolveBinaryDirectory();

    private static string ResolveBinaryDirectory()
    {
        var websiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        if (websiteName != null)
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, ".featherpod", "yt-dlp");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(localAppData, "FeatherPod", "yt-dlp");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "FeatherPod", "yt-dlp");
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(userHome, ".local", "share", "FeatherPod", "yt-dlp");
    }

    public string GetBinaryPath() => _binaryPath;

    /// <summary>
    /// Returns the deno binary path, or null if deno is not downloaded yet.
    /// yt-dlp uses deno as a JavaScript runtime for full YouTube format extraction.
    /// </summary>
    public string? GetDenoPath() => File.Exists(_denoPath) ? _denoPath : null;

    public bool IsAvailable()
    {
        if (_isAvailable.HasValue)
        {
            return _isAvailable.Value;
        }

        _isAvailable = File.Exists(_binaryPath);

        return _isAvailable.Value;
    }

    public async Task<bool> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (IsAvailable())
        {
            await CheckForUpdateIfDueAsync(cancellationToken);
            await EnsureDenoAvailableAsync(cancellationToken);

            return true;
        }

        _logger?.LogInformation("yt-dlp not found, downloading...");

        return await DownloadAsync(cancellationToken);
    }

    /// <summary>
    /// Fetches the latest yt-dlp binary from GitHub. Returns true if an update was applied.
    /// </summary>
    public async Task<bool> TryUpdateAsync(CancellationToken cancellationToken = default)
    {
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            var latestVersion = await GetLatestVersionAsync(cancellationToken);
            if (latestVersion == null)
            {
                _logger?.LogWarning("Failed to check for yt-dlp updates");

                return false;
            }

            var currentVersion = await GetCurrentVersionAsync();
            if (string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogDebug("yt-dlp is already at latest version: {Version}", currentVersion);
                _lastUpdateCheck = DateTime.UtcNow;

                return false;
            }

            _logger?.LogInformation("Updating yt-dlp from {Current} to {Latest}...", currentVersion ?? "unknown", latestVersion);

            return await DownloadBinaryAsync(latestVersion, cancellationToken);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<string?> GetCurrentVersionAsync()
    {
        var versionFile = Path.Combine(_binaryDirectory, VersionFileName);
        if (!File.Exists(versionFile))
        {
            return null;
        }

        var version = await File.ReadAllTextAsync(versionFile);

        return version.Trim();
    }

    private async Task<bool> DownloadAsync(CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (IsAvailable())
            {
                return true;
            }

            var latestVersion = await GetLatestVersionAsync(cancellationToken);
            if (latestVersion == null)
            {
                _logger?.LogError("Failed to determine latest yt-dlp version");

                return false;
            }

            return await DownloadBinaryAsync(latestVersion, cancellationToken);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<bool> DownloadBinaryAsync(string version, CancellationToken cancellationToken)
    {
        var binDir = _binaryDirectory;
        Directory.CreateDirectory(binDir);

        var assetName = GetPlatformAssetName();
        var downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/download/{version}/{assetName}";

        _logger?.LogInformation("Downloading yt-dlp {Version} from {Url}...", version, downloadUrl);

        try
        {
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var binaryPath = _binaryPath;
            await using (var fileStream = File.Create(binaryPath))
            {
                await response.Content.CopyToAsync(fileStream, cancellationToken);
            }

            // Mark executable on Linux/macOS
            if (!OperatingSystem.IsWindows())
            {
                var chmod = Process.Start("chmod", ["+x", binaryPath]);
                if (chmod != null)
                {
                    await chmod.WaitForExitAsync(cancellationToken);
                }
            }

            // Write version sidecar
            var versionFile = Path.Combine(binDir, VersionFileName);
            await File.WriteAllTextAsync(versionFile, version, cancellationToken);

            _isAvailable = true;
            _lastUpdateCheck = DateTime.UtcNow;

            _logger?.LogInformation("yt-dlp {Version} downloaded to {Path}", version, binaryPath);

            // Download deno (JS runtime for YouTube format extraction) if not already present
            await EnsureDenoAvailableAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download yt-dlp {Version}", version);

            return false;
        }
    }

    private async Task EnsureDenoAvailableAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_denoPath))
        {
            return;
        }

        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(DenoReleasesUrl, cancellationToken);
            if (release?.TagName == null)
            {
                _logger?.LogWarning("Failed to determine latest deno version");

                return;
            }

            var assetName = GetDenoAssetName();
            var downloadUrl = $"https://github.com/denoland/deno/releases/download/{release.TagName}/{assetName}";

            _logger?.LogInformation("Downloading deno {Version} for yt-dlp JS runtime...", release.TagName);

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Deno releases are zip files containing a single binary
            var zipPath = Path.Combine(_binaryDirectory, assetName);
            await using (var fileStream = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(fileStream, cancellationToken);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, _binaryDirectory, overwriteFiles: true);
            File.Delete(zipPath);

            // Mark executable on Linux/macOS
            if (!OperatingSystem.IsWindows())
            {
                var chmod = Process.Start("chmod", ["+x", _denoPath]);
                if (chmod != null)
                {
                    await chmod.WaitForExitAsync(cancellationToken);
                }
            }

            // Write version sidecar
            var versionFile = Path.Combine(_binaryDirectory, DenoVersionFileName);
            await File.WriteAllTextAsync(versionFile, release.TagName, cancellationToken);

            _logger?.LogInformation("deno {Version} downloaded to {Path}", release.TagName, _denoPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to download deno. yt-dlp will run without JS runtime (some formats may be unavailable)");
        }
    }

    private static string GetDenoAssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "deno-x86_64-pc-windows-msvc.zip";
        }

        if (OperatingSystem.IsLinux())
        {
            return "deno-x86_64-unknown-linux-gnu.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? "deno-aarch64-apple-darwin.zip"
                : "deno-x86_64-apple-darwin.zip";
        }

        throw new PlatformNotSupportedException("deno is not available for this platform");
    }

    private async Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(GitHubReleasesUrl, cancellationToken);

            return release?.TagName;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch latest yt-dlp release from GitHub");

            return null;
        }
    }

    private async Task CheckForUpdateIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastUpdateCheck < UpdateCheckInterval)
        {
            return;
        }

        _lastUpdateCheck = DateTime.UtcNow;

        // Fire-and-forget update check (don't block the caller)
        _ = Task.Run(async () =>
        {
            try
            {
                await TryUpdateAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Background yt-dlp update check failed");
            }
        }, cancellationToken);
    }

    private static string GetPlatformAssetName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsLinux())
        {
            return "yt-dlp_linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        throw new PlatformNotSupportedException("yt-dlp is not available for this platform");
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeatherPod/1.0");

        return client;
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }
}
