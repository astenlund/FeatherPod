using System.Text.Json;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;
    private readonly IBlobStorageService _blobService;
    private readonly IUserService _userService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly PushNotificationService _pushNotificationService;
    private readonly int _pollIntervalMs;

    private static readonly TimeSpan PushFallbackTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxSinceDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JobsController(IJobService jobService, IBlobStorageService blobService, IUserService userService, IJobProgressChannel progressChannel, IFeedEventChannel feedEventChannel, PushNotificationService pushNotificationService, IConfiguration configuration)
    {
        _jobService = jobService;
        _blobService = blobService;
        _userService = userService;
        _progressChannel = progressChannel;
        _feedEventChannel = feedEventChannel;
        _pushNotificationService = pushNotificationService;
        _pollIntervalMs = configuration.GetValue("PushPage:PollIntervalMs", 500);
    }

    /// <summary>
    /// Get normalization jobs for a feed.
    /// Without parameters, returns active (non-terminal) jobs only (CLI compatibility).
    /// With <paramref name="since"/>, returns all jobs (including terminal) within the time window.
    /// </summary>
    /// <param name="feedId">Feed ID</param>
    /// <param name="since">Optional duration string (e.g. "1h", "30m", "2h30m", "1d"). Max 24h.</param>
    [HttpGet("/api/feeds/{feedId}/jobs")]
    [ProducesResponseType<List<JobStatusResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActiveJobsByFeed(string feedId, [FromQuery] string? since = null)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var user = HttpContext.Items["User"] as User;
        if (user != null && user.Role != UserRole.Admin && !await _userService.ValidatePermissionAsync(user, feedId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have permission to view jobs for this feed" });
        }

        List<JobStatusEntity> entities;
        if (since != null)
        {
            if (!InputValidation.TryParseDuration(since, out var duration))
            {
                return BadRequest(new { error = "Invalid duration format. Use combinations of d/h/m, e.g. '1h', '30m', '2h30m', '1d'" });
            }

            if (duration > MaxSinceDuration)
            {
                duration = MaxSinceDuration;
            }

            entities = await _jobService.GetRecentJobsByFeedAsync(feedId, duration, HttpContext.RequestAborted);
        }
        else
        {
            entities = await _jobService.GetActiveJobsByFeedAsync(feedId, HttpContext.RequestAborted);
        }

        var response = entities
            .OrderBy(e => e.QueuedAt)
            .Select(JobStatusResponse.FromEntity)
            .ToList();

        return Ok(response);
    }

    /// <summary>
    /// Stream feed-level events (e.g., new job created) via Server-Sent Events.
    /// Used by push page clients for cross-tab/cross-device queue sync.
    /// No auth required — events contain no sensitive data, just notification triggers.
    /// </summary>
    [HttpGet("/api/feeds/{feedId}/events")]
    public async Task StreamFeedEvents(string feedId, CancellationToken cancellationToken)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var reader = _feedEventChannel.Subscribe(feedId);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(HeartbeatInterval);

                try
                {
                    if (await reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        while (reader.TryRead(out var eventType))
                        {
                            await WriteEventAsync(eventType, "{}", cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteCommentAsync("keepalive", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
        }
        finally
        {
            _feedEventChannel.Unsubscribe(feedId, reader);
        }
    }

    /// <summary>
    /// Get the status of a normalization job.
    /// </summary>
    [HttpGet("{jobId}")]
    [ProducesResponseType<JobStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(string jobId)
    {
        var entity = await _jobService.GetJobStatusAsync(jobId, HttpContext.RequestAborted);
        if (entity == null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found" });
        }

        return Ok(JobStatusResponse.FromEntity(entity));
    }

    /// <summary>
    /// Cancel a normalization job. Cleans up pending blobs.
    /// </summary>
    [HttpPost("{jobId}/cancel")]
    [ProducesResponseType<JobStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelJob(string jobId)
    {
        var entity = await _jobService.GetJobStatusAsync(jobId, HttpContext.RequestAborted);
        if (entity == null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found" });
        }

        // Check feed ownership
        var user = HttpContext.Items["User"] as User;
        if (user != null && user.Role != UserRole.Admin && (entity.FeedId == null || !user.OwnedFeeds.Contains(entity.FeedId)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have permission to cancel this job" });
        }

        var cancelled = await _jobService.CancelJobAsync(jobId, HttpContext.RequestAborted);
        if (cancelled == null)
        {
            return Conflict(new { error = "Job is already in a terminal state" });
        }

        // Publish cancellation to channel for instant SSE notification
        var cancelledResponse = JobStatusResponse.FromEntity(cancelled);
        _progressChannel.Publish(jobId, cancelledResponse);
        _pushNotificationService.TryNotifyJobTerminal(cancelledResponse);

        // Pending blob cleanup deferred to CleanupFunction (TranscriptionBackgroundService may still be reading)

        return Ok(cancelledResponse);
    }

    /// <summary>
    /// Stream real-time progress updates for a job via Server-Sent Events.
    /// Supports three modes: poll (default), push (HTTP POST from Function), and signalr.
    /// </summary>
    [HttpGet("{jobId}/progress")]
    public async Task StreamJobProgress(string jobId, CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        // Initial poll to get mode and interval
        var entity = await _jobService.GetJobStatusAsync(jobId, cancellationToken);
        if (entity == null)
        {
            await WriteEventAsync("error", """{"error":"Job not found"}""", cancellationToken);

            return;
        }

        var progressMode = entity.ProgressMode ?? "poll";
        var pollInterval = TimeSpan.FromMilliseconds(_pollIntervalMs);

        // Send initial state
        var initialResponse = JobStatusResponse.FromEntity(entity);
        await WriteEventAsync("progress", JsonSerializer.Serialize(initialResponse, JsonOptions), cancellationToken);

        if (IsTerminal(entity.Status))
        {
            await WriteEventAsync("done", "{}", cancellationToken);

            return;
        }

        if (progressMode is "push" or "signalr")
        {
            await StreamWithPushAsync(jobId, initialResponse, cancellationToken);
        }
        else
        {
            await StreamWithPollingAsync(jobId, entity, pollInterval, cancellationToken);
        }
    }

    private async Task StreamWithPollingAsync(string jobId, JobStatusEntity lastEntity, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, cancellationToken);

                var entity = await _jobService.GetJobStatusAsync(jobId, cancellationToken);
                if (entity == null)
                {
                    break;
                }

                JobStatusResponse? response = null;
                if (HasChanged(lastEntity, entity))
                {
                    response = JobStatusResponse.FromEntity(entity);
                    await WriteEventAsync("progress", JsonSerializer.Serialize(response, JsonOptions), cancellationToken);
                    lastEntity = entity;
                }

                if (IsTerminal(entity.Status))
                {
                    _pushNotificationService.TryNotifyJobTerminal(response ?? JobStatusResponse.FromEntity(entity));
                    await WriteEventAsync("done", "{}", cancellationToken);

                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    await WriteCommentAsync("keepalive", cancellationToken);
                    lastHeartbeat = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - normal
        }
    }

    private async Task StreamWithPushAsync(string jobId, JobStatusResponse lastResponse, CancellationToken cancellationToken)
    {
        var reader = _progressChannel.Subscribe(jobId);
        try
        {
            var lastHeartbeat = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for push with fallback timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(PushFallbackTimeout);

                JobStatusResponse? update = null;
                var fromFallbackPoll = false;
                try
                {
                    if (await reader.WaitToReadAsync(timeoutCts.Token))
                    {
                        // Drain all available items, keep the latest
                        while (reader.TryRead(out var item))
                        {
                            update = item;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout — fall back to Table Storage poll
                    var entity = await _jobService.GetJobStatusAsync(jobId, cancellationToken);
                    if (entity != null)
                    {
                        update = JobStatusResponse.FromEntity(entity);
                        fromFallbackPoll = true;
                    }
                }

                if (update != null && HasChanged(lastResponse, update))
                {
                    await WriteEventAsync("progress", JsonSerializer.Serialize(update, JsonOptions), cancellationToken);
                    lastResponse = update;
                }

                if (update != null && IsTerminal(update.Status))
                {
                    // Only notify from fallback poll -- channel updates already triggered
                    // TryNotifyJobTerminal at the ingestion point (ProgressHub/InternalController)
                    if (fromFallbackPoll)
                    {
                        _pushNotificationService.TryNotifyJobTerminal(update);
                    }
                    await WriteEventAsync("done", "{}", cancellationToken);

                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    await WriteCommentAsync("keepalive", cancellationToken);
                    lastHeartbeat = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - normal
        }
        finally
        {
            _progressChannel.Unsubscribe(jobId, reader);
        }
    }

    private async Task WriteEventAsync(string eventType, string data, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($"event: {eventType}\n", cancellationToken);
        await Response.WriteAsync($"data: {data}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task WriteCommentAsync(string comment, CancellationToken cancellationToken)
    {
        await Response.WriteAsync($": {comment}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static bool HasChanged(JobStatusEntity old, JobStatusEntity current)
    {
        return old.Status != current.Status ||
               old.NormalizationStage != current.NormalizationStage ||
               old.NormalizationProgress != current.NormalizationProgress ||
               old.ProgressMessage != current.ProgressMessage ||
               old.TranscriptionStatus != current.TranscriptionStatus ||
               old.NormalizationComplete != current.NormalizationComplete ||
               old.EpisodeId != current.EpisodeId ||
               old.Error != current.Error ||
               old.Title != current.Title;
    }

    private static bool HasChanged(JobStatusResponse? last, JobStatusResponse current)
    {
        if (last == null)
        {
            return true;
        }

        return last.Status != current.Status ||
               last.Stage != current.Stage ||
               last.ProgressPercent != current.ProgressPercent ||
               last.ProgressMessage != current.ProgressMessage ||
               last.TranscriptionStatus != current.TranscriptionStatus ||
               last.NormalizationComplete != current.NormalizationComplete ||
               last.EpisodeId != current.EpisodeId ||
               last.Error != current.Error ||
               last.Title != current.Title;
    }

    private static bool IsTerminal(string? status)
    {
        return status is nameof(JobStatus.Completed) or nameof(JobStatus.Failed) or nameof(JobStatus.Cancelled);
    }
}
