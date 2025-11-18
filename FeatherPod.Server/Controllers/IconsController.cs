using FeatherPod.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/feeds/{feedId}/icon")]
public class IconsController : ControllerBase
{
    private readonly EpisodeService _episodeService;
    private readonly IBlobStorageService _blobService;
    private readonly ILogger<IconsController> _logger;
    private readonly string _baseUrl;

    public IconsController(
        EpisodeService episodeService,
        IBlobStorageService blobService,
        ILogger<IconsController> logger,
        IConfiguration configuration)
    {
        _episodeService = episodeService;
        _blobService = blobService;
        _logger = logger;
        _baseUrl = configuration.GetSection("Podcast")["BaseUrl"]
            ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadIcon(
        string feedId,
        [FromForm] IFormFile? file)
    {
        try
        {
            _logger.LogInformation("Icon upload request for feed '{FeedId}'", feedId);

            var feed = await _episodeService.GetFeedAsync(feedId);
            if (feed == null)
            {
                _logger.LogWarning("Feed '{FeedId}' not found for icon upload", feedId);
                return NotFound(new { error = $"Feed '{feedId}' not found" });
            }

            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("No file uploaded for feed '{FeedId}'", feedId);
                return BadRequest(new { error = "No file uploaded" });
            }

            _logger.LogInformation("Uploading icon for feed '{FeedId}', size: {Size} bytes, type: {ContentType}",
                feedId, file.Length, file.ContentType);

            // Validate file type
            var allowedTypes = new[] { "image/png", "image/jpeg", "image/jpg" };
            if (!allowedTypes.Contains(file.ContentType.ToLower()))
            {
                _logger.LogWarning("Invalid file type '{ContentType}' for feed '{FeedId}'", file.ContentType, feedId);
                return BadRequest(new { error = "Only PNG and JPEG images are allowed" });
            }

            // Save uploaded file to temp location
            var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, "icon.png");

            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }

            _logger.LogDebug("Saved temporary icon to {TempPath}", tempPath);

            try
            {
                await _blobService.UploadIconAsync(feedId, tempPath);
                _logger.LogInformation("Successfully uploaded icon for feed '{FeedId}'", feedId);
                return Ok(new { message = $"Icon uploaded for feed '{feedId}'", iconUrl = $"{_baseUrl}/{feedId}/icon.png" });
            }
            finally
            {
                // Clean up temp file
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading icon for feed '{FeedId}'", feedId);
            return Problem(detail: ex.Message, statusCode: 500);
        }
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteIcon(string feedId)
    {
        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            _logger.LogWarning("Feed '{FeedId}' not found for icon deletion", feedId);
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        await _blobService.DeleteIconAsync(feedId);
        _logger.LogInformation("Deleted icon for feed '{FeedId}'", feedId);

        return NoContent();
    }
}
