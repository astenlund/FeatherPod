using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFeed(string feedId)
    {
        var feed = await _episodeService.GetFeedAsync(feedId);
        return feed != null ? Ok(feed) : NotFound(new { error = $"Feed '{feedId}' not found" });
    }

    [HttpPost]
    [ProducesResponseType<FeedConfig>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateFeed([FromBody] FeedConfig feedConfig)
    {
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFeed(string feedId, [FromBody] FeedConfig feedConfig)
    {
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFeed(string feedId)
    {
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
}
