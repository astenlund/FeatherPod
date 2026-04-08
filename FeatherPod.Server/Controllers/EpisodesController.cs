using System.Text.Json;
using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared.Models;
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
    private readonly ITranscriptionChannel _transcriptionChannel;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly ILogger<EpisodesController> _logger;
    private readonly string _baseUrl;
    private readonly string? _progressMode;
    private readonly int _progressIntervalMs;

    public EpisodesController(EpisodeService episodeService, IBlobStorageService blobStorageService, IJobService jobService, IUserService userService, IFeedEventChannel feedEventChannel, IAiService aiService, ITranscriptionChannel transcriptionChannel, ISpeechTranscriptionService speechService, ILogger<EpisodesController> logger, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _blobStorageService = blobStorageService;
        _jobService = jobService;
        _userService = userService;
        _feedEventChannel = feedEventChannel;
        _aiService = aiService;
        _transcriptionChannel = transcriptionChannel;
        _speechService = speechService;
        _logger = logger;
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

                // Upload to pending location (AI title runs in background, doesn't block 202)
                await _blobStorageService.UploadPendingAudioAsync(feedId, jobId, file.FileName, tempPath);

                var effectiveTitle = ResolveDeferredTitle(title, jobId, file.FileName);

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

                // Create entity BEFORE queue send (entity must exist when Function processes the message)
                var transcriptionStatus = _speechService.IsAvailable ? TranscriptionStatuses.Queued : null;
                await _jobService.CreateJobStatusAsync(
                    jobId, feedId, file.FileName, effectiveTitle,
                    _progressMode, _progressIntervalMs,
                    description: description,
                    summary: summary,
                    publishedDate: effectivePublishedDate,
                    source: source.ToString(),
                    originalFileSize: fileSize,
                    episodeId: effectiveEpisodeId,
                    transcriptionStatus: transcriptionStatus,
                    cancellationToken: HttpContext.RequestAborted);

                await _jobService.QueueNormalizationJobAsync(job, HttpContext.RequestAborted);

                // Submit transcription request if Speech is available
                if (_speechService.IsAvailable)
                {
                    await _transcriptionChannel.SubmitAsync(new TranscriptionRequest
                    {
                        JobId = jobId,
                        FeedId = feedId,
                        FileName = file.FileName,
                        EpisodeId = effectiveEpisodeId
                    }, HttpContext.RequestAborted);
                }

                var response = new JobStatusResponse
                {
                    JobId = jobId,
                    FeedId = feedId,
                    Status = nameof(JobStatus.Queued),
                    EpisodeId = effectiveEpisodeId,
                    FileName = file.FileName,
                    Title = effectiveTitle,
                    QueuedAt = job.QueuedAt
                };

                _feedEventChannel.Publish(feedId, "job-added");

                return Accepted($"/api/jobs/{jobId}", response);
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = await EpisodeService.GenerateTitleAsync(file.FileName, _aiService, HttpContext.RequestAborted);
            }

            // Synchronous upload (no normalization)
            var episode = await _episodeService.AddEpisodeAsync(feedId, tempPath, title, description, summary, publishedDate, episodeId, source, cancellationToken: HttpContext.RequestAborted);
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
    public async Task<IActionResult> SuggestTitle(string feedId, string id, [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] SuggestTitleRequest? request)
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

        // YouTube titles are already good -- skip AI suggestion
        if (episode.MediaSource == MediaSource.YouTube)
        {
            return Ok(new { suggestedTitle = string.Empty });
        }

        // Use note from request body if provided, otherwise fall back to stored episode note
        var note = episode.Note;
        var requestNote = request?.Note?.Trim();
        if (!string.IsNullOrEmpty(requestNote))
        {
            note = requestNote.Length > MaxNoteLength ? requestNote[..MaxNoteLength] : requestNote;
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
        [FromBody] MoveEpisodeRequest request)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var targetFeedId = request.TargetFeedId;
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
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
        [FromBody] CopyEpisodeRequest request)
    {
        if (!InputValidation.IsValidFeedId(feedId))
        {
            return BadRequest(new { error = InputValidation.GetFeedIdValidationError(feedId) });
        }

        var targetFeedId = request.TargetFeedId;
        if (string.IsNullOrEmpty(targetFeedId))
        {
            return BadRequest(new { error = "targetFeedId is required in request body" });
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

    /// <summary>
    /// Returns an initial placeholder title and, when AI is available, fires off a background
    /// task that replaces it with the AI-generated (or heuristic fallback) title.
    /// </summary>
    private string ResolveDeferredTitle(string? userTitle, string jobId, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(userTitle))
        {
            return userTitle;
        }

        if (_aiService.IsAvailable)
        {
            _ = UpdateJobTitleFromAiAsync(jobId, fileName);

            return fileName;
        }

        return EpisodeService.ParseTitleFromFilename(fileName);
    }

    private async Task UpdateJobTitleFromAiAsync(string jobId, string fileName)
    {
        try
        {
            var title = await EpisodeService.GenerateTitleAsync(fileName, _aiService);

            await _jobService.UpdateJobStatusAsync(jobId, entity => entity.Title = title);
            _logger.LogInformation("Updated job {JobId} title: {Title}", jobId, title);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Failed to update title for job {JobId}", jobId);
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
