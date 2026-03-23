namespace FeatherPod.Server.Services;

/// <summary>
/// Background service that periodically cleans up stale temp files left behind by crashed
/// or failed upload/normalization operations. Scans the FeatherPod subdirectory under the
/// system temp path and deletes entries older than 1 hour.
/// </summary>
public class TempFileCleanupService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "FeatherPod");

    private readonly ILogger<TempFileCleanupService> _logger;

    public TempFileCleanupService(ILogger<TempFileCleanupService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Temp file cleanup service started. Cleanup interval: {Interval}, max age: {MaxAge}", CleanupInterval, MaxAge);

        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = CleanupStaleEntries(BasePath, MaxAge, _logger);

                if (result.DeletedDirs > 0 || result.DeletedFiles > 0 || result.Errors > 0)
                {
                    _logger.LogInformation(
                        "Temp cleanup: deleted {Dirs} directories and {Files} files, {Errors} errors",
                        result.DeletedDirs, result.DeletedFiles, result.Errors);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during temp file cleanup");
            }

            await Task.Delay(CleanupInterval, stoppingToken);
        }

        _logger.LogInformation("Temp file cleanup service stopped");
    }

    public static (int DeletedDirs, int DeletedFiles, int Errors) CleanupStaleEntries(
        string basePath, TimeSpan maxAge, ILogger? logger = null)
    {
        if (!Directory.Exists(basePath))
        {
            return (0, 0, 0);
        }

        var cutoff = DateTime.UtcNow - maxAge;
        int deletedDirs = 0;
        int deletedFiles = 0;
        int errors = 0;

        foreach (var dir in Directory.EnumerateDirectories(basePath))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    logger?.LogInformation("Deleted stale temp directory: {Path}", dir);
                    deletedDirs++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Failed to delete temp directory: {Path}", dir);
                errors++;
            }
        }

        foreach (var file in Directory.EnumerateFiles(basePath))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    logger?.LogInformation("Deleted stale temp file: {Path}", file);
                    deletedFiles++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Failed to delete temp file: {Path}", file);
                errors++;
            }
        }

        return (deletedDirs, deletedFiles, errors);
    }
}
