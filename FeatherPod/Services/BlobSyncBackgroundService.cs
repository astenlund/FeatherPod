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
                _logger.LogInformation("Starting blob storage sync for all feeds...");

                // Create a scope to get the singleton EpisodeService
                using var scope = _serviceProvider.CreateScope();
                var episodeService = scope.ServiceProvider.GetRequiredService<EpisodeService>();

                // Get all feeds and sync each one
                var feeds = await episodeService.GetFeedsAsync();
                foreach (var feed in feeds)
                {
                    try
                    {
                        await episodeService.SyncWithBlobStorageAsync(feed.Id);
                        _logger.LogInformation("Synced feed: {FeedId}", feed.Id);
                    }
                    catch (Exception feedEx)
                    {
                        _logger.LogError(feedEx, "Error syncing feed: {FeedId}", feed.Id);
                    }
                }

                _logger.LogInformation("Blob storage sync completed successfully for {Count} feeds", feeds.Count);
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
