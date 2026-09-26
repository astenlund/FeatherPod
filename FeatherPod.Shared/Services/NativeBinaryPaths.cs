namespace FeatherPod.Shared.Services;

/// <summary>
/// Platform rules shared by the native binary managers (<see cref="FFmpegBinaryManager"/>, <see cref="YtDlpBinaryManager"/>)
/// for naming downloaded executables and choosing where they are stored.
/// </summary>
internal static class NativeBinaryPaths
{
    /// <summary>
    /// Returns the local file name of a native executable: the tool name with <c>.exe</c> on Windows, the bare name elsewhere.
    /// </summary>
    public static string ExecutableFileName(string tool)
    {
        return OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;
    }

    /// <summary>
    /// Returns the platform-specific directory for storing a downloaded tool under <paramref name="toolFolder"/>.
    /// </summary>
    public static string GetToolDirectory(string toolFolder)
    {
        return GetToolDirectory(toolFolder, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), Environment.GetFolderPath);
    }

    /// <summary>
    /// Resolves the tool directory from explicit environment inputs, so every platform branch can be tested on any host.
    /// </summary>
    internal static string GetToolDirectory(string toolFolder, Func<string, string?> getEnvironmentVariable, bool isWindows, Func<Environment.SpecialFolder, string> getFolderPath)
    {
        // Azure App Service/Functions: Use HOME directory for persistent storage
        var websiteName = getEnvironmentVariable("WEBSITE_SITE_NAME");
        if (websiteName != null)
        {
            var home = getEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                // Windows: D:\home, Linux: /home
                return Path.Combine(home, ".featherpod", toolFolder);
            }
        }

        if (isWindows)
        {
            var localAppData = getFolderPath(Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(localAppData, "FeatherPod", toolFolder);
        }

        // Linux/macOS: Use XDG Base Directory spec ($XDG_DATA_HOME or ~/.local/share)
        var xdgDataHome = getEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "FeatherPod", toolFolder);
        }

        var userHome = getFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(userHome, ".local", "share", "FeatherPod", toolFolder);
    }
}
