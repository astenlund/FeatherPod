using System.Text.Json;

using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/feeds/{feedId}/episodes")]
public class EpisodesController : ControllerBase
{
    private readonly EpisodeService _episodeService;
    private readonly string _baseUrl;

    public EpisodesController(EpisodeService episodeService, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _baseUrl = configuration.GetSection("Podcast")["BaseUrl"]
            ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");
    }

    [HttpGet]
    [ProducesResponseType<List<Episode>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListEpisodes(string feedId)
    {
        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        var episodes = await _episodeService.GetAllEpisodesAsync(feedId);

        // Populate URL for each episode
        var episodesWithUrls = episodes
            .Select(e => e with { Url = e.GetAudioUrl(_baseUrl) })
            .ToList();

        return Ok(episodesWithUrls);
    }

    [HttpPost]
    [ProducesResponseType<Episode>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 500 * 1024 * 1024)]
    public async Task<IActionResult> UploadEpisode(
        string feedId,
        [FromForm] IFormFile? file,
        [FromForm] string? title,
        [FromForm] string? description,
        [FromForm] string? summary,
        [FromForm] DateTime? publishedDate)
    {
        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded" });
        }

        // Save uploaded file to temp location with original filename
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, file.FileName);

        await using (var stream = System.IO.File.Create(tempPath))
        {
            await file.CopyToAsync(stream);
        }

        try
        {
            var episode = await _episodeService.AddEpisodeAsync(
                feedId,
                tempPath,
                title,
                description,
                summary,
                publishedDate);

            var episodeWithUrl = episode with { Url = episode.GetAudioUrl(_baseUrl) };

            return CreatedAtAction(nameof(ListEpisodes), new { feedId }, episodeWithUrl);
        }
        finally
        {
            // Clean up temp directory
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEpisode(string feedId, string id)
    {
        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        var deleted = await _episodeService.DeleteEpisodeAsync(feedId, id);
        return deleted
            ? Ok(new { message = $"Episode '{id}' deleted from feed '{feedId}'" })
            : NotFound(new { error = $"Episode '{id}' not found in feed '{feedId}'" });
    }

    [HttpPost("{id}/move")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MoveEpisode(
        string feedId,
        string id,
        [FromBody] JsonElement body)
    {
        if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
        }

        var targetFeedId = targetFeedIdElement.GetString();
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId cannot be empty" });
        }

        try
        {
            var movedEpisode = await _episodeService.MoveEpisodeAsync(id, feedId, targetFeedId);
            var episodeWithUrl = movedEpisode with { Url = movedEpisode.GetAudioUrl(_baseUrl) };

            return Ok(new
            {
                message = $"Episode '{id}' moved from '{feedId}' to '{targetFeedId}'",
                episode = episodeWithUrl
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/copy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CopyEpisode(
        string feedId,
        string id,
        [FromBody] JsonElement body)
    {
        if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
        }

        var targetFeedId = targetFeedIdElement.GetString();
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId cannot be empty" });
        }

        try
        {
            var copiedEpisode = await _episodeService.CopyEpisodeAsync(id, feedId, targetFeedId);
            var episodeWithUrl = copiedEpisode with { Url = copiedEpisode.GetAudioUrl(_baseUrl) };

            return Ok(new
            {
                message = $"Episode '{id}' copied from '{feedId}' to '{targetFeedId}'",
                episode = episodeWithUrl
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
