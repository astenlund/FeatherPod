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
    private readonly IJobService _jobService;
    private readonly IFeedEventChannel _feedEventChannel;
    private readonly EpisodeService _episodeService;
    private readonly Channel<YouTubeDownloadJob> _downloadChannel;

    public YouTubeController(
        IJobService jobService,
        IFeedEventChannel feedEventChannel,
        EpisodeService episodeService,
        Channel<YouTubeDownloadJob> downloadChannel)
    {
        _jobService = jobService;
        _feedEventChannel = feedEventChannel;
        _episodeService = episodeService;
        _downloadChannel = downloadChannel;
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

        var videoId = YtDlpService.ValidateUrl(request.Url);
        if (videoId == null)
        {
            return BadRequest(new { error = "Not a valid YouTube video URL. Playlists, channels, shorts, and search URLs are not supported." });
        }

        var jobId = Guid.NewGuid().ToString("N");
        var formatName = request.Format.ToString().ToLowerInvariant();
        var episodeId = Episode.GenerateYouTubeId(feedId, videoId, formatName);

        var fileName = $"{videoId}{request.Format.GetExtension()}";
        var title = request.Title ?? videoId;
        await _jobService.CreateJobStatusAsync(
            new CreateJobOptions { JobId = jobId, FeedId = feedId, FileName = fileName, Title = title },
            HttpContext.RequestAborted);

        var job = new YouTubeDownloadJob
        {
            JobId = jobId,
            FeedId = feedId,
            VideoId = videoId,
            Format = request.Format,
            EpisodeId = episodeId,
            Title = title,
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
}
