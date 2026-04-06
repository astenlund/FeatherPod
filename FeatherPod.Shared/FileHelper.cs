using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared;

/// <summary>
/// File system utility helpers shared across server, functions, and shared services.
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Best-effort temp file cleanup. Silently no-ops on null/empty/missing paths
    /// and logs a warning (without rethrowing) on delete failures. Intended for
    /// finally-block cleanup where the caller must not propagate IO errors.
    /// </summary>
    public static void TryDeleteFile(string? path, ILogger logger)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete temp file: {Path}", path);
        }
    }
}
