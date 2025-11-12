namespace FeatherPod.Services;

/// <summary>
/// Background service that periodically syncs the in-memory episode list with blob storage.
/// This ensures that manual changes to blob storage (e.g., via Azure Storage Explorer)
/// are eventually reflected in the application.
/// </summary>
public class BlobSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BlobSyncBackgroundService> _logger;
    private readonly TimeSpan _syncInterval = TimeSpan.FromHours(1);

    public BlobSyncBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<BlobSyncBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Blob sync background service started. Sync interval: {Interval}", _syncInterval);

        // Wait a bit before first sync (app just started and already loaded episodes)
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting blob storage sync...");

                // Create a scope to get the singleton EpisodeService
                using var scope = _serviceProvider.CreateScope();
                var episodeService = scope.ServiceProvider.GetRequiredService<EpisodeService>();

                await episodeService.SyncWithBlobStorageAsync();

                _logger.LogInformation("Blob storage sync completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during blob storage sync");
            }

            // Wait for next sync interval
            await Task.Delay(_syncInterval, stoppingToken);
        }

        _logger.LogInformation("Blob sync background service stopped");
    }
}
