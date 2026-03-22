using System.Text.Json;
using FeatherPod.Shared.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/feeds/{feedId}/episodes")]
public class EpisodesController : ControllerBase
{
    private const int MaxNoteLength = 500;
    private readonly EpisodeService _episodeService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IJobService _jobService;
    private readonly IUserService _userService;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly IAiService _aiService;
    private readonly string _baseUrl;
    private readonly string? _progressMode;
    private readonly int _progressIntervalMs;

    public EpisodesController(EpisodeService episodeService, IBlobStorageService blobStorageService, IJobService jobService, IUserService userService, IFeedEventChannel feedEventChannel, IAiService aiService, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _blobStorageService = blobStorageService;
        _jobService = jobService;
        _userService = userService;
        _feedEventChannel = feedEventChannel;
        _aiService = aiService;
        _baseUrl = configuration.GetSection("Podcast")["BaseUrl"]
            ?? throw new InvalidOperationException("Podcast.BaseUrl must be configured in appsettings.json");

        var mode = configuration.GetValue("PushPage:ProgressMode", "signalr");
        _progressMode = mode is "push" or "signalr" ? mode : null;
        _progressIntervalMs = configuration.GetValue("PushPage:ProgressIntervalMs", 250);
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

    [HttpGet("recent-uploads")]
    [ProducesResponseType<List<Episode>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecentUploads(
        string feedId,
        [FromQuery] UploadSource? source = null,
        [FromQuery] int limit = 10)
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

        var episodes = await _episodeService.GetRecentUploadsAsync(feedId, source, limit);

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
        [FromQuery] bool normalize = false,
        [FromQuery] UploadSource source = UploadSource.CLI)
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
                var effectivePublishedDate = publishedDate ?? (feed.UseFileMetadataForPublishDate && EpisodeService.TryGetPublishedDateFromFile(tempPath, out var extractedDate)
                    ? extractedDate.Value
                    : DateTime.UtcNow);

                // Upload to pending location and generate AI title in parallel
                var blobUploadTask = _blobStorageService.UploadPendingAudioAsync(feedId, jobId, file.FileName, tempPath);
                var aiTitleTask = string.IsNullOrWhiteSpace(title) && _aiService.IsAvailable
                    ? _aiService.SuggestTitleAsync(file.FileName, cancellationToken: HttpContext.RequestAborted)
                    : Task.FromResult<string?>(null);

                await Task.WhenAll(blobUploadTask, aiTitleTask);
                var aiTitle = aiTitleTask.Result;
                var effectiveTitle = aiTitle ?? (string.IsNullOrWhiteSpace(title) ? EpisodeService.ParseTitleFromFilename(file.FileName) : title);

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
                    QueuedAt = DateTime.UtcNow,
                    Source = source,
                    ProgressMode = _progressMode,
                    ProgressIntervalMs = _progressIntervalMs
                };

                await _jobService.QueueNormalizationJobAsync(job, HttpContext.RequestAborted);
                await _jobService.CreateJobStatusAsync(jobId, feedId, file.FileName, _progressMode, _progressIntervalMs, HttpContext.RequestAborted);

                var response = new JobStatusResponse
                {
                    JobId = jobId,
                    FeedId = feedId,
                    Status = nameof(JobStatus.Queued),
                    EpisodeId = effectiveEpisodeId,
                    FileName = file.FileName,
                    QueuedAt = job.QueuedAt
                };

                _feedEventChannel.Publish(feedId, "job-added");

                return Accepted($"/api/jobs/{jobId}", response);
            }

            // AI title generation for sync upload (when user didn't provide a title)
            if (string.IsNullOrWhiteSpace(title) && _aiService.IsAvailable)
            {
                title = await _aiService.SuggestTitleAsync(file.FileName, cancellationToken: HttpContext.RequestAborted);
            }

            // Synchronous upload (no normalization)
            var episode = await _episodeService.AddEpisodeAsync(feedId, tempPath, title, description, summary, publishedDate, episodeId, source, HttpContext.RequestAborted);
            var episodeWithUrl = episode with { Url = episode.GetAudioUrl(_baseUrl) };

            _feedEventChannel.Publish(feedId, "episode-added");

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
        if (!deleted)
        {
            return NotFound(new { error = $"Episode '{id}' not found in feed '{feedId}'" });
        }

        _feedEventChannel.Publish(feedId, "episode-deleted");

        return Ok(new { message = $"Episode '{id}' deleted from feed '{feedId}'" });
    }

    [HttpPatch("{id}")]
    [ProducesResponseType<Episode>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEpisode(
        string feedId,
        string id,
        [FromBody] JsonElement body)
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

        string? title = null;
        string? note = null;

        if (body.TryGetProperty("title", out var titleElement))
        {
            title = titleElement.GetString()?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                return BadRequest(new { error = "title cannot be empty" });
            }
        }

        if (body.TryGetProperty("note", out var noteElement))
        {
            var rawNote = noteElement.ValueKind == JsonValueKind.Null ? "" : noteElement.GetString()?.Trim() ?? "";
            note = rawNote.Length > MaxNoteLength ? rawNote[..MaxNoteLength] : rawNote;
        }

        if (title == null && note == null)
        {
            return BadRequest(new { error = "At least one of 'title' or 'note' is required" });
        }

        var updated = await _episodeService.UpdateEpisodeMetadataAsync(feedId, id, title, note);
        if (updated == null)
        {
            return NotFound(new { error = $"Episode '{id}' not found in feed '{feedId}'" });
        }

        _feedEventChannel.Publish(feedId, "episode-updated");
        var episodeWithUrl = updated with { Url = updated.GetAudioUrl(_baseUrl) };

        return Ok(episodeWithUrl);
    }

    [HttpPost("{id}/suggest-title")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SuggestTitle(string feedId, string id)
    {
        if (!_aiService.IsAvailable)
        {
            return NotFound(new { error = "AI title suggestions not available" });
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

        var episode = await _episodeService.GetEpisodeByIdAsync(feedId, id);
        if (episode == null)
        {
            return NotFound(new { error = $"Episode '{id}' not found in feed '{feedId}'" });
        }

        // Read optional note from request body, fall back to stored episode note
        string? note = episode.Note;
        if (Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
            if (doc.RootElement.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String)
            {
                var requestNote = noteElement.GetString()?.Trim();
                if (!string.IsNullOrEmpty(requestNote))
                {
                    note = requestNote.Length > MaxNoteLength ? requestNote[..MaxNoteLength] : requestNote;
                }
            }
        }

        var suggestedTitle = await _aiService.SuggestTitleAsync(episode.FileName, note, HttpContext.RequestAborted)
            ?? EpisodeService.ParseTitleFromFilename(episode.FileName);

        return Ok(new { suggestedTitle });
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

        if (!await HasTargetFeedPermissionAsync(targetFeedId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"You do not have permission on target feed '{targetFeedId}'" });
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

        if (!await HasTargetFeedPermissionAsync(targetFeedId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"You do not have permission on target feed '{targetFeedId}'" });
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

    private async Task<bool> HasTargetFeedPermissionAsync(string targetFeedId)
    {
        if (HttpContext.Items["User"] is not User user)
        {
            return false;
        }

        return await _userService.ValidatePermissionAsync(user, targetFeedId);
    }
}
