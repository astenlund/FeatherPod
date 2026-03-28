using System.Threading.Channels;
using FeatherPod.Server.Models;
using FeatherPod.Server.Services;
using FeatherPod.Server.Validation;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/feeds/{feedId}/youtube")]
public class YouTubeController : ControllerBase
{
    private readonly YtDlpBinaryManager _binaryManager;
    private readonly YtDlpService _ytDlpService;
    private readonly IJobService _jobService;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly EpisodeService _episodeService;
    private readonly Channel<YouTubeDownloadJob> _downloadChannel;
    private readonly YouTubeCookieService _cookieService;

    public YouTubeController(
        YtDlpBinaryManager binaryManager,
        YtDlpService ytDlpService,
        IJobService jobService,
        IFeedEventChannel feedEventChannel,
        EpisodeService episodeService,
        Channel<YouTubeDownloadJob> downloadChannel,
        YouTubeCookieService cookieService)
    {
        _binaryManager = binaryManager;
        _ytDlpService = ytDlpService;
        _jobService = jobService;
        _feedEventChannel = feedEventChannel;
        _episodeService = episodeService;
        _downloadChannel = downloadChannel;
        _cookieService = cookieService;
    }

    [HttpPost]
    [ProducesResponseType<JobStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ImportYouTubeVideo(string feedId, [FromBody] YouTubeImportRequest request)
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

        // Validate URL and extract video ID
        var videoId = YtDlpService.ValidateUrl(request.Url);
        if (videoId == null)
        {
            return BadRequest(new { error = "Not a valid YouTube video URL. Playlists, channels, shorts, and search URLs are not supported." });
        }

        // Ensure yt-dlp is available (lazy download on first use)
        var available = await _binaryManager.EnsureAvailableAsync(HttpContext.RequestAborted);
        if (!available)
        {
            return StatusCode(503, new { error = "YouTube import is temporarily unavailable (yt-dlp download failed)" });
        }

        // Reconstruct canonical URL from validated video ID to prevent command injection.
        // The raw user URL is never passed to yt-dlp.
        var canonicalUrl = YtDlpService.GetCanonicalUrl(videoId);

        // Get cookie file path if cookies are available
        string? cookiePath = null;
        var tempCookieDir = Path.Combine(Path.GetTempPath(), "FeatherPod", $"cookies-{Guid.NewGuid():N}");
        try
        {
            cookiePath = await _cookieService.GetCookieFilePathAsync(tempCookieDir, HttpContext.RequestAborted);
        }
        catch
        {
            // Non-fatal: proceed without cookies
        }

        try
        {
            // Fetch metadata to validate the video is accessible
            var (metadata, metaError) = await _ytDlpService.GetMetadataAsync(canonicalUrl, cookiePath, HttpContext.RequestAborted);
            if (metadata == null)
            {
                if (metaError != null && YtDlpService.IsBotDetectionError(metaError))
                {
                    return BadRequest(new { error = YtDlpService.BotDetectionErrorMessage, authRequired = true });
                }

                return BadRequest(new { error = metaError ?? "Video is unavailable" });
            }

            // Generate IDs
            var jobId = Guid.NewGuid().ToString("N");
            var formatName = request.Format.ToString().ToLowerInvariant();
            var episodeId = Episode.GenerateYouTubeId(feedId, videoId, formatName);

            // Create job status in Table Storage
            var fileName = $"{videoId}{request.Format.GetExtension()}";
            var title = metadata.Title ?? "Untitled";
            await _jobService.CreateJobStatusAsync(jobId, feedId, fileName, title, cancellationToken: HttpContext.RequestAborted);

            // Enqueue download job
            var job = new YouTubeDownloadJob
            {
                JobId = jobId,
                FeedId = feedId,
                VideoId = videoId,
                Format = request.Format,
                EpisodeId = episodeId,
                Title = title,
                Channel = metadata.Channel,
                Description = metadata.Description,
                Duration = TimeSpan.FromSeconds(metadata.Duration),
                UploadDate = metadata.GetUploadDateTime(),
                QueuedAt = DateTime.UtcNow
            };

            await _downloadChannel.Writer.WriteAsync(job, HttpContext.RequestAborted);

            _feedEventChannel.Publish(feedId, "job-added");

            var response = new JobStatusResponse
            {
                JobId = jobId,
                FeedId = feedId,
                Status = nameof(JobStatus.Queued),
                EpisodeId = episodeId,
                FileName = fileName,
                Title = title,
                QueuedAt = job.QueuedAt
            };

            return Accepted($"/api/jobs/{jobId}", response);
        }
        finally
        {
            // Clean up temp cookie file
            if (Directory.Exists(tempCookieDir))
            {
                try
                {
                    Directory.Delete(tempCookieDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }
}
