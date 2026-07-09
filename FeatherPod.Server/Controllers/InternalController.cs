using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

using static FeatherPod.Server.Validation.SecurityHelpers;

namespace FeatherPod.Server.Controllers;

/// <summary>
/// Internal endpoints for service-to-service communication.
/// Protected by X-Internal-Key header.
/// </summary>
[ApiController]
[Route("api/internal")]
public class InternalController : ControllerBase
{
    private readonly IJobProgressChannel _progressChannel;
    private readonly PushNotificationService _pushNotificationService;
    private readonly JobCompletionService _jobCompletionService;
    private readonly string? _internalKey;

    public InternalController(IJobProgressChannel progressChannel, PushNotificationService pushNotificationService, JobCompletionService jobCompletionService, IConfiguration configuration)
    {
        _progressChannel = progressChannel;
        _pushNotificationService = pushNotificationService;
        _jobCompletionService = jobCompletionService;
        _internalKey = configuration["Internal:Key"];
    }

    /// <summary>
    /// Receive a pushed progress update from Azure Function.
    /// Publishes to in-memory channel for active SSE connections.
    /// </summary>
    [HttpPost("jobs/{jobId}/progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult PushJobProgress(string jobId, [FromBody] JobStatusResponse progress)
    {
        if (ValidateInternalKey() is { } authError)
        {
            return authError;
        }

        _progressChannel.Publish(jobId, progress);
        _pushNotificationService.TryNotifyJobTerminal(progress);

        return Ok();
    }

    /// <summary>
    /// Receive normalization completion signal from Azure Function.
    /// Delegates to JobCompletionService for fork-join episode creation.
    /// </summary>
    [HttpPost("jobs/{jobId}/normalization-complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> NormalizationComplete(string jobId, [FromBody] NormalizationCompleteRequest request)
    {
        if (ValidateInternalKey() is { } authError)
        {
            return authError;
        }

        await _jobCompletionService.HandleNormalizationCompleteAsync(jobId, request, HttpContext.RequestAborted);

        return Ok();
    }

    /// <summary>
    /// Trigger join-logic check for a job (idempotent).
    /// Called by CleanupFunction after marking stale transcriptions as failed.
    /// </summary>
    [HttpPost("jobs/{jobId}/check-completion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CheckCompletion(string jobId)
    {
        if (ValidateInternalKey() is { } authError)
        {
            return authError;
        }

        await _jobCompletionService.TryCompleteJobAsync(jobId, HttpContext.RequestAborted);

        return Ok();
    }

    /// <summary>
    /// Validates the X-Internal-Key header against the configured internal key.
    /// Returns <c>null</c> when the request is authorized (or when no internal key
    /// is configured, which disables the check), otherwise an <see cref="UnauthorizedObjectResult"/>.
    /// </summary>
    private IActionResult? ValidateInternalKey()
    {
        if (string.IsNullOrEmpty(_internalKey))
        {
            return null;
        }

        var providedKey = Request.Headers["X-Internal-Key"].FirstOrDefault();
        if (ConstantTimeEquals(providedKey, _internalKey))
        {
            return null;
        }

        return Unauthorized(new { error = "Invalid or missing X-Internal-Key header" });
    }
}
