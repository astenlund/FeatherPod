using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FeedsController : ControllerBase
{
    private readonly EpisodeService _episodeService;

    public FeedsController(EpisodeService episodeService)
    {
        _episodeService = episodeService;
    }

    [HttpGet]
    [ProducesResponseType<List<FeedConfig>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListFeeds()
    {
        var feeds = await _episodeService.GetFeedsAsync();
        return Ok(feeds);
    }

    [HttpGet("{feedId}")]
    [ProducesResponseType<FeedConfig>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFeed(string feedId)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var feed = await _episodeService.GetFeedAsync(feedId);
        return feed != null ? Ok(feed) : NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    [HttpPost]
    [ProducesResponseType<FeedConfig>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFeed([FromBody] FeedConfig feedConfig)
    {
        if (!InputValidation.IsValidFeedId(feedConfig.Id))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedConfig.Id) });
        }

        try
        {
            var created = await _episodeService.CreateFeedAsync(feedConfig);
            return CreatedAtAction(nameof(GetFeed), new { feedId = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{feedId}")]
    [ProducesResponseType<FeedConfig>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFeed(string feedId, [FromBody] FeedConfig feedConfig)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        try
        {
            var updated = await _episodeService.UpdateFeedAsync(feedId, feedConfig);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("{feedId}/rename")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RenameFeed(string feedId, [FromQuery] string newId)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        if (!InputValidation.IsValidFeedId(newId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(newId) });
        }

        try
        {
            await _episodeService.RenameFeedAsync(feedId, newId);
            return Ok(new { message = $"Feed renamed from '{feedId}' to '{newId}'" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{feedId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFeed(string feedId)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        try
        {
            await _episodeService.DeleteFeedAsync(feedId);

            return Ok(new { message = $"Feed '{feedId}' deleted" });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Check data integrity - verifies episode metadata loads correctly and audio blobs exist.
    /// Admins can check all feeds or filter by feedId. FeedOwners can only check their owned feeds.
    /// </summary>
    [HttpGet("check-integrity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CheckDataIntegrity([FromQuery] string? feedId = null)
    {
        if (HttpContext.Items["User"] is not User user)
        {
            return Unauthorized();
        }

        var feedsToCheck = GetAccessibleFeeds(user, feedId);
        if (feedsToCheck == null)
        {
            return Forbid();
        }

        var report = await _episodeService.CheckDataIntegrityAsync(feedsToCheck);

        return Ok(new
        {
            totalEpisodes = report.TotalEpisodes,
            validEpisodes = report.ValidEpisodes,
            missingBlobs = report.MissingBlobs.Count,
            issues = report.MissingBlobs.Select(e => new
            {
                e.FeedId,
                e.EpisodeId,
                e.FileName,
                e.Title
            })
        });
    }

    /// <summary>
    /// Determines which feeds the user can access for integrity check.
    /// Returns null if the user doesn't have access to the requested feed.
    /// Returns empty list to check all accessible feeds.
    /// </summary>
    private static List<string>? GetAccessibleFeeds(User user, string? requestedFeedId)
    {
        if (user.Role == UserRole.Admin)
        {
            // Admin can check any feed, or all feeds if none specified
            return requestedFeedId != null ? [requestedFeedId] : [];
        }

        // FeedOwner can only check owned feeds
        if (requestedFeedId != null)
        {
            // Check if they own the requested feed
            if (user.OwnedFeeds.Contains(requestedFeedId))
            {
                return [requestedFeedId];
            }

            return null; // No access
        }

        // No feed specified - return all owned feeds
        return user.OwnedFeeds.ToList();
    }
}
