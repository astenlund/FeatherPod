using System.Text.Json;
using Azure;
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
    private readonly IJobProgressChannel _progressChannel;
    private readonly PushNotificationService _pushNotificationService;
    private readonly ILogger<EpisodesController> _logger;
    private readonly string _baseUrl;
    private readonly string? _progressMode;
    private readonly int _progressIntervalMs;

    public EpisodesController(EpisodeService episodeService, IBlobStorageService blobStorageService, IJobService jobService, IUserService userService, IFeedEventChannel feedEventChannel, IAiService aiService, ITranscriptionChannel transcriptionChannel, ISpeechTranscriptionService speechService, IJobProgressChannel progressChannel, PushNotificationService pushNotificationService, ILogger<EpisodesController> logger, IConfiguration configuration)
    {
        _episodeService = episodeService;
        _blobStorageService = blobStorageService;
        _jobService = jobService;
        _userService = userService;
        _feedEventChannel = feedEventChannel;
        _aiService = aiService;
        _transcriptionChannel = transcriptionChannel;
        _speechService = speechService;
        _progressChannel = progressChannel;
        _pushNotificationService = pushNotificationService;
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
        [FromForm] string? clientUploadId,
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

        if (clientUploadId != null && !InputValidation.IsValidClientUploadId(clientUploadId))
        {
            return BadRequest(new { error = InputValidation.GetClientUploadIdValidationError() });
        }

        // Save uploaded file to temp location with original filename
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, file.FileName);

        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(stream, HttpContext.RequestAborted);
            }

            // Async normalization flow - queue job and return 202
            if (normalize)
            {
                return await QueueNormalizationJobAsync(feed, file.FileName, tempPath, new UploadFields(title, description, summary, publishedDate, episodeId, clientUploadId, source));
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
    /// Store an upload for async normalization: upload it to the pending location, create its job entity, start the AI
    /// title of an untitled upload, queue the normalization message and submit transcription, then answer 202 with the job.
    /// An untitled upload's job carries the title parsed from its file name until the AI title replaces it.
    /// An abort observed by the end of the pending-blob upload stops the work and deletes the pending blob, since nothing
    /// refers to it. From the entity insert on, the steps run to their end without the request's token, so an abort
    /// cannot leave a half-created job (an entity whose queue message was never sent and that never finishes); an abort
    /// observed after the insert or between the later steps cancels the job instead, since the client never learns its id
    /// from a response that cannot be delivered, and when that cancel does not take effect the remaining steps still run.
    /// A job cancelled this way keeps its pending blob for the scheduled cleanup, as after a user cancel.
    /// An abort the server never observes (the client gone while the request still looks alive) leaves the job running;
    /// a client that sent a clientUploadId recognizes such a job by it in recent-jobs listings.
    /// </summary>
    private async Task<IActionResult> QueueNormalizationJobAsync(FeedConfig feed, string fileName, string tempPath, UploadFields fields)
    {
        var requestAborted = HttpContext.RequestAborted;
        var feedId = feed.Id;
        var jobId = Guid.NewGuid().ToString("N");
        var fileSize = new FileInfo(tempPath).Length;
        var effectiveEpisodeId = fields.EpisodeId ?? Episode.GenerateId(feedId, fileName, fileSize);
        var effectivePublishedDate = fields.PublishedDate ?? (feed.UseFileMetadataForPublishDate && EpisodeService.TryGetPublishedDateFromFile(tempPath, out var extractedDate)
            ? extractedDate.Value
            : DateTime.UtcNow);

        try
        {
            await _blobStorageService.UploadPendingAudioAsync(feedId, jobId, fileName, tempPath, requestAborted);
            requestAborted.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            await DeletePendingBlobsAfterAbortAsync(feedId, jobId);

            throw;
        }

        var deferAiTitle = string.IsNullOrWhiteSpace(fields.Title) && _aiService.IsAvailable;
        var effectiveTitle = string.IsNullOrWhiteSpace(fields.Title) ? EpisodeService.ParseTitleFromFilename(fileName) : fields.Title;

        var job = new NormalizationJob
        {
            JobId = jobId,
            FeedId = feedId,
            FileName = fileName,
            OriginalFileSize = fileSize,
            EpisodeId = effectiveEpisodeId,
            Title = effectiveTitle,
            Description = fields.Description,
            Summary = fields.Summary,
            PublishedDate = effectivePublishedDate,
            QueuedAt = DateTime.UtcNow,
            Source = fields.Source,
            ProgressMode = _progressMode,
            ProgressIntervalMs = _progressIntervalMs
        };

        // Create entity BEFORE queue send (entity must exist when Function processes the message)
        var transcriptionStatus = _speechService.IsAvailable ? TranscriptionStatuses.Queued : null;
        await _jobService.CreateJobStatusAsync(
            new CreateJobOptions
            {
                JobId = jobId,
                FeedId = feedId,
                FileName = fileName,
                Title = effectiveTitle,
                ProgressMode = _progressMode,
                ProgressIntervalMs = _progressIntervalMs,
                Description = fields.Description,
                Summary = fields.Summary,
                PublishedDate = effectivePublishedDate,
                Source = fields.Source.ToString(),
                OriginalFileSize = fileSize,
                EpisodeId = effectiveEpisodeId,
                TranscriptionStatus = transcriptionStatus,
                ClientUploadId = fields.ClientUploadId
            },
            CancellationToken.None);

        if (await CancelJobIfUploadAbortedAsync(jobId))
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        // The AI title replaces the placeholder in the background without blocking the 202; it starts only now, because
        // the update needs the job entity to exist and a job cancelled with its aborted upload needs no title
        if (deferAiTitle)
        {
            _ = UpdateJobTitleFromAiAsync(jobId, fileName);
        }

        await _jobService.QueueNormalizationJobAsync(job, CancellationToken.None);

        if (await CancelJobIfUploadAbortedAsync(jobId))
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }

        // Submit transcription request if Speech is available
        if (_speechService.IsAvailable)
        {
            await _transcriptionChannel.SubmitAsync(new TranscriptionRequest
            {
                JobId = jobId,
                FeedId = feedId,
                FileName = fileName,
                EpisodeId = effectiveEpisodeId
            }, CancellationToken.None);

            if (await CancelJobIfUploadAbortedAsync(jobId))
            {
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
        }

        var response = new JobStatusResponse
        {
            JobId = jobId,
            FeedId = feedId,
            Status = nameof(JobStatus.Queued),
            EpisodeId = effectiveEpisodeId,
            FileName = fileName,
            Title = effectiveTitle,
            QueuedAt = job.QueuedAt,
            ClientUploadId = fields.ClientUploadId
        };

        _feedEventChannel.Publish(feedId, "job-added");

        return Accepted($"/api/jobs/{jobId}", response);
    }

    /// <summary>
    /// Cancel a job whose upload request has been aborted, so that a job the client cannot learn about does not run to a
    /// published episode. Like the user-initiated cancel (JobsController.CancelJob), a cancellation publishes the
    /// cancelled status for progress streams and goes through the terminal push-notification entry point.
    /// Returns true only when the job was cancelled. While the request is still alive it does nothing, and when the cancel
    /// does not take effect it returns false too, so the caller completes the job's remaining steps rather than leave it
    /// half-created; such a job then runs like one whose abort the server never observed.
    /// </summary>
    private async Task<bool> CancelJobIfUploadAbortedAsync(string jobId)
    {
        if (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        JobStatusEntity? cancelled;
        try
        {
            cancelled = await _jobService.CancelJobAsync(jobId, CancellationToken.None);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Failed to cancel job {JobId} after its upload request was aborted; completing its remaining steps", jobId);

            return false;
        }

        if (cancelled == null)
        {
            _logger.LogWarning("Job {JobId} was not cancelled after its upload request was aborted; completing its remaining steps", jobId);

            return false;
        }

        _logger.LogInformation("Cancelled job {JobId}: its upload request was aborted before the response was sent", jobId);
        var cancelledResponse = JobStatusResponse.FromEntity(cancelled);
        _progressChannel.Publish(jobId, cancelledResponse);
        _pushNotificationService.TryNotifyJobTerminal(cancelledResponse);

        return true;
    }

    private async Task DeletePendingBlobsAfterAbortAsync(string feedId, string jobId)
    {
        try
        {
            await _blobStorageService.DeletePendingJobBlobsAsync(feedId, jobId);
        }
        catch (RequestFailedException ex)
        {
            // Orphaned pending blobs are deleted later by the Functions app's scheduled cleanup
            _logger.LogWarning(ex, "Failed to delete pending blobs of aborted upload {JobId}", jobId);
        }
    }

    /// <summary>
    /// Replaces an untitled job's placeholder title with the AI-generated (or heuristic fallback) title. Runs in the
    /// background and must start only after the job entity exists, since an update of a missing entity is lost.
    /// </summary>
    private async Task UpdateJobTitleFromAiAsync(string jobId, string fileName)
    {
        try
        {
            var title = await EpisodeService.GenerateTitleAsync(fileName, _aiService);

            if (await _jobService.UpdateJobStatusAsync(jobId, entity => entity.Title = title) == null)
            {
                _logger.LogWarning("AI title for job {JobId} was lost: the job could not be updated", jobId);
            }
            else
            {
                _logger.LogInformation("Updated job {JobId} title: {Title}", jobId, title);
            }
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

    /// <summary>
    /// The optional form fields of an upload that carry into its job.
    /// </summary>
    private sealed record UploadFields(string? Title, string? Description, string? Summary, DateTime? PublishedDate, string? EpisodeId, string? ClientUploadId, UploadSource Source);
}
