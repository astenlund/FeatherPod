using System.Text.Json;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JobsController(IJobService jobService)
    {
        _jobService = jobService;
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
    /// Stream real-time progress updates for a job via Server-Sent Events.
    /// </summary>
    [HttpGet("{jobId}/progress")]
    public async Task StreamJobProgress(string jobId, CancellationToken cancellationToken)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var lastHeartbeat = DateTime.UtcNow;
        JobStatusEntity? lastEntity = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var entity = await _jobService.GetJobStatusAsync(jobId, cancellationToken);

                if (entity == null)
                {
                    await WriteEventAsync("error", """{"error":"Job not found"}""", cancellationToken);

                    break;
                }

                if (lastEntity == null || HasChanged(lastEntity, entity))
                {
                    var response = JobStatusResponse.FromEntity(entity);
                    var json = JsonSerializer.Serialize(response, JsonOptions);
                    await WriteEventAsync("progress", json, cancellationToken);
                    lastEntity = entity;
                }

                if (entity.Status is nameof(JobStatus.Completed) or nameof(JobStatus.Failed))
                {
                    await WriteEventAsync("done", "{}", cancellationToken);

                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    await WriteCommentAsync("keepalive", cancellationToken);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected - normal
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
               old.Stage != current.Stage ||
               old.ProgressPercent != current.ProgressPercent ||
               old.ProgressMessage != current.ProgressMessage ||
               old.Error != current.Error;
    }
}
