using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FeatherPod.Server.Models;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace FeatherPod.Server.Services;

public class PushNotificationService
{
    private readonly TimeSpan _activityWindow;
    private readonly IBlobStorageService _blobStorageService;
    private readonly EpisodeService _episodeService;
    private readonly IJobService _jobService;
    private readonly ILogger<PushNotificationService> _logger;
    private readonly PushServiceClient _pushClient;
    private readonly VapidAuthentication? _vapidAuth;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, List<PushSubscriptionInfo>> _cache = new();
    private readonly ConcurrentDictionary<string, NotificationSession> _sessions = new();

    public bool IsEnabled => _vapidAuth is not null;

    public PushNotificationService(IBlobStorageService blobStorageService, EpisodeService episodeService, IJobService jobService, IConfiguration config, ILogger<PushNotificationService> logger)
    {
        _blobStorageService = blobStorageService;
        _episodeService = episodeService;
        _jobService = jobService;
        _logger = logger;
        _pushClient = new PushServiceClient();
        _activityWindow = TimeSpan.FromHours(config.GetValue("PushNotifications:ActivityWindowHours", 2.0));

        var publicKey = config["PushNotifications:VapidPublicKey"];
        var privateKey = config["PushNotifications:VapidPrivateKey"];
        var subject = config["PushNotifications:VapidSubject"];

        if (!string.IsNullOrEmpty(publicKey) && !string.IsNullOrEmpty(privateKey) && !string.IsNullOrEmpty(subject))
        {
            _vapidAuth = new VapidAuthentication(publicKey, privateKey) { Subject = subject };
            _logger.LogInformation("Push notifications enabled (VAPID configured)");
        }
        else
        {
            _logger.LogInformation("Push notifications disabled (VAPID keys not configured)");
        }
    }

    // ========================================================================
    // SUBSCRIPTION MANAGEMENT
    // ========================================================================

    public async Task SubscribeAsync(string feedId, PushSubscriptionRequest request)
    {
        if (!IsEnabled)
        {
            return;
        }

        await EnsureFeedLoadedAsync(feedId);

        bool changed;
        await _lock.WaitAsync();
        try
        {
            var subscriptions = _cache[feedId];
            var now = DateTime.UtcNow;

            var existingIndex = subscriptions.FindIndex(s => s.Endpoint == request.Endpoint);

            if (existingIndex >= 0 && now - subscriptions[existingIndex].LastActiveAt < _activityWindow / 4)
            {
                changed = false;
            }
            else
            {
                var entry = new PushSubscriptionInfo
                {
                    Endpoint = request.Endpoint,
                    P256dh = request.P256dh,
                    Auth = request.Auth,
                    CreatedAt = existingIndex >= 0 ? subscriptions[existingIndex].CreatedAt : now,
                    LastActiveAt = now,
                };

                if (existingIndex >= 0)
                {
                    subscriptions[existingIndex] = entry;
                }
                else
                {
                    subscriptions.Add(entry);
                }

                changed = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (changed)
        {
            await PersistAsync(feedId);
            _logger.LogInformation("Push subscription registered for feed {FeedId}: {Endpoint}", feedId, TruncateEndpoint(request.Endpoint));
        }
    }

    public async Task UnsubscribeAsync(string feedId, string endpoint)
    {
        if (!IsEnabled)
        {
            return;
        }

        await EnsureFeedLoadedAsync(feedId);

        int removed;
        bool hasRemainingSubscriptions;
        await _lock.WaitAsync();
        try
        {
            var subscriptions = _cache[feedId];
            removed = subscriptions.RemoveAll(s => s.Endpoint == endpoint);
            var cutoff = DateTime.UtcNow - _activityWindow;
            hasRemainingSubscriptions = subscriptions.Any(s => s.LastActiveAt >= cutoff);
        }
        finally
        {
            _lock.Release();
        }

        if (removed > 0)
        {
            await PersistAsync(feedId);
            _logger.LogInformation("Push subscription removed for feed {FeedId}: {Endpoint}", feedId, TruncateEndpoint(endpoint));
        }

        if (!hasRemainingSubscriptions)
        {
            _sessions.TryRemove(feedId, out _);
        }
    }

    // ========================================================================
    // SESSION TRACKING (Upload Batching)
    // ========================================================================

    public async Task TrackJobsAsync(string feedId, IReadOnlyList<string> jobIds, int uploadsRemaining = 0)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (!await HasActiveSubscriptionsAsync(feedId))
        {
            return;
        }

        // Step 1: add jobIds to session under lock, update uploadsRemaining
        List<string> newJobIds;
        int sessionTotal;
        await _lock.WaitAsync();
        try
        {
            var session = _sessions.GetOrAdd(feedId, _ => new NotificationSession());
            newJobIds = jobIds.Where(id => session.PendingJobIds.Add(id)).ToList();
            session.UploadsRemaining = uploadsRemaining;
            sessionTotal = session.PendingJobIds.Count;
        }
        finally
        {
            _lock.Release();
        }

        if (newJobIds.Count == 0 && uploadsRemaining > 0)
        {
            return;
        }

        if (newJobIds.Count > 0)
        {
            _logger.LogInformation("Tracking {Count} new jobs for feed {FeedId} (session total: {Total}, uploads remaining: {Remaining})",
                newJobIds.Count, feedId, sessionTotal, uploadsRemaining);
        }

        // Step 2: reconcile -- check each new job's current status (I/O outside lock)
        var terminalJobs = new List<CompletedJobInfo>();
        var terminalJobIds = new List<string>();
        foreach (var jobId in newJobIds)
        {
            var entity = await _jobService.GetJobStatusAsync(jobId);
            if (entity is null)
            {
                continue;
            }

            if (entity.Status is nameof(JobStatus.Completed) or nameof(JobStatus.Failed) or nameof(JobStatus.Cancelled))
            {
                var title = await ResolveEpisodeTitleAsync(feedId, entity.EpisodeId, entity.FileName);
                terminalJobs.Add(new CompletedJobInfo(entity.Status, title));
                terminalJobIds.Add(jobId);
            }
        }

        if (terminalJobs.Count == 0)
        {
            return;
        }

        // Step 3: move terminal jobs from pending to completed under lock
        List<CompletedJobInfo>? snapshot = null;
        await _lock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(feedId, out var session))
            {
                for (var i = 0; i < terminalJobIds.Count; i++)
                {
                    if (session.PendingJobIds.Remove(terminalJobIds[i]))
                    {
                        session.CompletedJobs.Add(terminalJobs[i]);
                    }
                }

                if (IsSessionComplete(session))
                {
                    snapshot = [.. session.CompletedJobs];
                    _sessions.TryRemove(feedId, out _);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        if (snapshot is not null)
        {
            await SendSessionNotificationAsync(feedId, snapshot);
        }
    }

    public void TryNotifyJobTerminal(JobStatusResponse progress)
    {
        try
        {
            if (!IsEnabled || progress.FeedId is null || progress.JobId is null)
            {
                return;
            }

            if (progress.Status is not (nameof(JobStatus.Completed) or nameof(JobStatus.Failed) or nameof(JobStatus.Cancelled)))
            {
                return;
            }

            if (_sessions.ContainsKey(progress.FeedId))
            {
                _ = TryNotifyJobTerminalAsync(progress);
            }
            else
            {
                // No session -- server-restart fallback: send immediate per-job notification
                _ = TryNotifyFallbackAsync(progress);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TryNotifyJobTerminal for job {JobId}", progress.JobId);
        }
    }

    private async Task TryNotifyJobTerminalAsync(JobStatusResponse progress)
    {
        try
        {
            var feedId = progress.FeedId!;
            var jobId = progress.JobId!;

            // Resolve title outside lock
            var title = await ResolveEpisodeTitleAsync(feedId, progress.EpisodeId, progress.FileName);

            // Update session under lock
            List<CompletedJobInfo>? snapshot = null;
            await _lock.WaitAsync();
            try
            {
                if (_sessions.TryGetValue(feedId, out var session) && session.PendingJobIds.Remove(jobId))
                {
                    session.CompletedJobs.Add(new CompletedJobInfo(progress.Status, title));

                    if (IsSessionComplete(session))
                    {
                        snapshot = [.. session.CompletedJobs];
                        _sessions.TryRemove(feedId, out _);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }

            if (snapshot is not null)
            {
                await SendSessionNotificationAsync(feedId, snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TryNotifyJobTerminalAsync for job {JobId}", progress.JobId);
        }
    }

    private async Task TryNotifyFallbackAsync(JobStatusResponse progress)
    {
        try
        {
            var feedId = progress.FeedId!;

            if (!await HasActiveSubscriptionsAsync(feedId))
            {
                return;
            }

            // Skip cancelled jobs in fallback mode (user-initiated, no notification needed)
            if (progress.Status == nameof(JobStatus.Cancelled))
            {
                return;
            }

            var title = await ResolveEpisodeTitleAsync(feedId, progress.EpisodeId, progress.FileName);
            var notifTitle = progress.Status == nameof(JobStatus.Completed) ? "Upload complete" : "Upload failed";

            await TryNotifyAsync(feedId, notifTitle, title, $"/{feedId}/icon-192.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TryNotifyFallbackAsync for job {JobId}", progress.JobId);
        }
    }

    // ========================================================================
    // NOTIFICATION DELIVERY
    // ========================================================================

    public async Task TryNotifyAsync(string feedId, string title, string body, string icon)
    {
        try
        {
            if (!IsEnabled)
            {
                return;
            }

            await EnsureFeedLoadedAsync(feedId);

            List<PushSubscriptionInfo> activeSubscriptions;
            await _lock.WaitAsync();
            try
            {
                var cutoff = DateTime.UtcNow - _activityWindow;
                activeSubscriptions = _cache[feedId].Where(s => s.LastActiveAt >= cutoff).ToList();
            }
            finally
            {
                _lock.Release();
            }

            if (activeSubscriptions.Count == 0)
            {
                return;
            }

            var payload = JsonSerializer.Serialize(new { title, body, icon, feedId });
            var staleEndpoints = new List<string>();

            var tasks = activeSubscriptions.Select(async sub =>
            {
                try
                {
                    var pushSubscription = new PushSubscription { Endpoint = sub.Endpoint };
                    pushSubscription.SetKey(PushEncryptionKeyName.P256DH, sub.P256dh);
                    pushSubscription.SetKey(PushEncryptionKeyName.Auth, sub.Auth);

                    await _pushClient.RequestPushMessageDeliveryAsync(pushSubscription, new PushMessage(payload), _vapidAuth!);
                }
                catch (PushServiceClientException ex) when (ex.StatusCode == HttpStatusCode.Gone)
                {
                    lock (staleEndpoints)
                    {
                        staleEndpoints.Add(sub.Endpoint);
                    }
                    _logger.LogInformation("Removing stale push subscription (410 Gone): {Endpoint}", TruncateEndpoint(sub.Endpoint));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send push notification to {Endpoint}", TruncateEndpoint(sub.Endpoint));
                }
            });
            await Task.WhenAll(tasks);

            if (staleEndpoints.Count > 0)
            {
                await _lock.WaitAsync();
                try
                {
                    if (_cache.TryGetValue(feedId, out var subscriptions))
                    {
                        subscriptions.RemoveAll(s => staleEndpoints.Contains(s.Endpoint));
                    }
                }
                finally
                {
                    _lock.Release();
                }

                await PersistAsync(feedId);
            }

            _logger.LogInformation("Sent push notifications for feed {FeedId}: {Count} recipients", feedId, activeSubscriptions.Count - staleEndpoints.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in TryNotifyAsync for feed {FeedId}", feedId);
        }
    }

    // ========================================================================
    // HELPERS
    // ========================================================================

    private async Task<bool> HasActiveSubscriptionsAsync(string feedId)
    {
        if (!IsEnabled)
        {
            return false;
        }

        await EnsureFeedLoadedAsync(feedId);

        await _lock.WaitAsync();
        try
        {
            if (!_cache.TryGetValue(feedId, out var subscriptions))
            {
                return false;
            }

            var cutoff = DateTime.UtcNow - _activityWindow;

            return subscriptions.Any(s => s.LastActiveAt >= cutoff);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string> ResolveEpisodeTitleAsync(string feedId, string? episodeId, string? fileName)
    {
        if (episodeId is not null)
        {
            var episode = await _episodeService.GetEpisodeByIdAsync(feedId, episodeId);
            if (episode?.Title is not null)
            {
                return episode.Title;
            }
        }

        return fileName is not null
            ? EpisodeService.ParseTitleFromFilename(fileName)
            : "Unknown file";
    }

    private static bool IsSessionComplete(NotificationSession session) =>
        session.PendingJobIds.Count == 0 && session.UploadsRemaining == 0 && session.CompletedJobs.Count > 0;

    private async Task SendSessionNotificationAsync(string feedId, List<CompletedJobInfo> completions)
    {
        var (title, body) = ComposeNotificationSummary(completions);

        await TryNotifyAsync(feedId, title, body, $"/{feedId}/icon-192.png");
        _logger.LogInformation("Session notification sent for feed {FeedId}: {Body}", feedId, body);
    }

    internal static (string Title, string Body) ComposeNotificationSummary(List<CompletedJobInfo> jobs)
    {
        var completed = jobs.Count(j => j.Status == nameof(JobStatus.Completed));
        var failed = jobs.Count(j => j.Status == nameof(JobStatus.Failed));

        return (completed, failed) switch
        {
            (1, 0) => ("Upload complete", jobs.First(j => j.Status == nameof(JobStatus.Completed)).Title ?? "1 episode pushed"),
            (> 1, 0) => ("Uploads complete", $"{completed} episodes pushed"),
            (0, 1) => ("Upload failed", jobs.First(j => j.Status == nameof(JobStatus.Failed)).Title ?? "1 episode failed"),
            (0, > 1) => ("Uploads failed", $"{failed} episodes failed"),
            (> 0, > 0) => ("Uploads finished", $"{completed} pushed, {failed} failed"),
            _ => ("Uploads finished", "All uploads processed"),
        };
    }

    private async Task EnsureFeedLoadedAsync(string feedId)
    {
        if (_cache.ContainsKey(feedId))
        {
            return;
        }

        var json = await _blobStorageService.LoadPushSubscriptionsAsync(feedId);

        await _lock.WaitAsync();
        try
        {
            if (_cache.ContainsKey(feedId))
            {
                return;
            }

            var subscriptions = !string.IsNullOrEmpty(json)
                ? JsonSerializer.Deserialize<List<PushSubscriptionInfo>>(json) ?? []
                : [];

            _cache.TryAdd(feedId, subscriptions);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PersistAsync(string feedId)
    {
        string json;
        await _lock.WaitAsync();
        try
        {
            json = JsonSerializer.Serialize(_cache.TryGetValue(feedId, out var subs) ? subs : []);
        }
        finally
        {
            _lock.Release();
        }

        await _blobStorageService.SavePushSubscriptionsAsync(feedId, json);
    }

    private static string TruncateEndpoint(string endpoint) =>
        endpoint.Length > 60 ? $"{endpoint.Truncate(60).TrimEnd()}..." : endpoint;

    private class NotificationSession
    {
        public HashSet<string> PendingJobIds { get; } = [];
        public List<CompletedJobInfo> CompletedJobs { get; } = [];
        public int UploadsRemaining { get; set; }
    }

    internal record CompletedJobInfo(string Status, string? Title);
}
