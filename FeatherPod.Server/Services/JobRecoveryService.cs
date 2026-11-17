namespace FeatherPod.Server.Services;

/// <summary>
/// Runs <see cref="JobRetryService.RecoverStalledRetriesAsync"/> while the server is up, not only at startup: once at start and then
/// every <c>JobRecovery:IntervalMinutes</c> (default 15). Each pass's bounds are measured from the jobs' queue times, so a job left
/// behind before a restart is recovered once its bound passes, whenever that is.
/// </summary>
public sealed class JobRecoveryService : BackgroundService
{
    private readonly JobRetryService _retryService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobRecoveryService> _logger;
    private readonly TimeSpan _interval;

    public JobRecoveryService(JobRetryService retryService, TimeProvider timeProvider, IConfiguration configuration, ILogger<JobRecoveryService> logger)
    {
        _retryService = retryService;
        _timeProvider = timeProvider;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("JobRecovery:IntervalMinutes", 15)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        do
        {
            try
            {
                await _retryService.RecoverStalledRetriesAsync(_timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job retry recovery pass failed");
            }
        }
        while (await WaitForNextPassAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextPassAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
            return false;
        }
    }
}
