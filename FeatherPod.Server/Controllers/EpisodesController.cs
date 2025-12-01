using System.Text.Json;
using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/feeds/{feedId}/episodes")]
public class EpisodesController : ControllerBase
{
    private readonly EpisodeService _episodeService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IJobService _jobService;
    private readonly string _baseUrl;

    public EpisodesController(EpisodeService episodeService, IBlobStorageService blobStorageService, IJobService jobService, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _blobStorageService = blobStorageService;
        _jobService = jobService;
        _baseUrl = configuration.GetSection("Podcast")["BaseUrl"]
            ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");
    }

    [HttpGet]
    [ProducesResponseType<List<Episode>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListEpisodes(string feedId)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

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
    [ProducesResponseType<JobStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = 500 * 1024 * 1024)]
    public async Task<IActionResult> UploadEpisode(
        string feedId,
        [FromForm] IFormFile? file,
        [FromForm] string? title,
        [FromForm] string? description,
        [FromForm] string? summary,
        [FromForm] DateTime? publishedDate,
        [FromForm] string? episodeId,
        [FromQuery] bool normalize = false)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var feed = await _episodeService.GetFeedAsync(feedId);
        if (feed == null)
        {
            return NotFound(new { error = $"Feed '{feedId}' not found" });
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file uploaded" });
        }

        if (!InputValidation.IsValidFilename(file.FileName))
        {
            return BadRequest(new { error = InputValidation.GetFilenameValidationError(file.FileName) });
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
            // Async normalization flow - queue job and return 202
            if (normalize)
            {
                var jobId = Guid.NewGuid().ToString("N");
                var fileSize = new FileInfo(tempPath).Length;
                var effectiveEpisodeId = episodeId ?? Episode.GenerateId(feedId, file.FileName, fileSize);
                var effectiveTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title;
                var effectivePublishedDate = publishedDate ?? DateTime.UtcNow;

                // Upload to pending location
                await _blobStorageService.UploadPendingAudioAsync(feedId, jobId, file.FileName, tempPath);

                // Queue the normalization job first, then create status entry
                // This order prevents orphaned status entries if queue send fails
                var job = new NormalizationJob
                {
                    JobId = jobId,
                    FeedId = feedId,
                    FileName = file.FileName,
                    OriginalFileSize = fileSize,
                    EpisodeId = effectiveEpisodeId,
                    Title = effectiveTitle,
                    Description = description,
                    Summary = summary,
                    PublishedDate = effectivePublishedDate,
                    QueuedAt = DateTime.UtcNow
                };

                await _jobService.QueueNormalizationJobAsync(job, HttpContext.RequestAborted);
                await _jobService.CreateJobStatusAsync(jobId, feedId, HttpContext.RequestAborted);

                var response = new JobStatusResponse
                {
                    JobId = jobId,
                    FeedId = feedId,
                    Status = nameof(JobStatus.Queued),
                    EpisodeId = effectiveEpisodeId,
                    QueuedAt = job.QueuedAt
                };

                return Accepted($"/api/jobs/{jobId}", response);
            }

            // Synchronous upload (no normalization)
            var episode = await _episodeService.AddEpisodeAsync(feedId, tempPath, title, description, summary, publishedDate, episodeId, HttpContext.RequestAborted);
            var episodeWithUrl = episode with { Url = episode.GetAudioUrl(_baseUrl) };

            return CreatedAtAction(nameof(ListEpisodes), new { feedId }, episodeWithUrl);
        }
        finally
        {
            // Clean up temp directory - best effort, don't fail the request on cleanup errors
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                    // File may be locked or in use - OS will clean up temp eventually
                }
                catch (UnauthorizedAccessException)
                {
                    // Permission issue - OS will clean up temp eventually
                }
            }
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEpisode(string feedId, string id)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

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
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
        }

        var targetFeedId = targetFeedIdElement.GetString();
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId cannot be empty" });
        }

        if (!InputValidation.IsValidFeedId(targetFeedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(targetFeedId) });
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
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        if (!body.TryGetProperty("targetFeedId", out var targetFeedIdElement))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
        }

        var targetFeedId = targetFeedIdElement.GetString();
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId cannot be empty" });
        }

        if (!InputValidation.IsValidFeedId(targetFeedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(targetFeedId) });
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
