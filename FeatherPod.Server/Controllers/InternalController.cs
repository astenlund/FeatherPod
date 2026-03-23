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
    private readonly EpisodeService _episodeService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly string? _internalKey;

    public InternalController(EpisodeService episodeService, IJobProgressChannel progressChannel, IFeedEventChannel feedEventChannel, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _progressChannel = progressChannel;
        _feedEventChannel = feedEventChannel;
        _internalKey = configuration["Internal:Key"];
    }

    /// <summary>
    /// Refresh the in-memory cache for a feed.
    /// Called by Azure Function after normalization completes.
    /// </summary>
    [HttpPost("feeds/{feedId}/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefreshFeedCache(string feedId)
    {
        // Validate internal key if configured
        if (!string.IsNullOrEmpty(_internalKey))
        {
            var providedKey = Request.Headers["X-Internal-Key"].FirstOrDefault();
            if (!ConstantTimeEquals(providedKey, _internalKey))
            {
                return Unauthorized(new { error = "Invalid or missing X-Internal-Key header" });
            }
        }

        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        await _episodeService.SyncWithBlobStorageAsync(feedId);

        _feedEventChannel.Publish(feedId, "episode-added");

        return Ok(new { message = $"Cache refreshed for feed '{feedId}'" });
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
        if (!string.IsNullOrEmpty(_internalKey))
        {
            var providedKey = Request.Headers["X-Internal-Key"].FirstOrDefault();
            if (!ConstantTimeEquals(providedKey, _internalKey))
            {
                return Unauthorized(new { error = "Invalid or missing X-Internal-Key header" });
            }
        }

        _progressChannel.Publish(jobId, progress);

        return Ok();
    }
}
