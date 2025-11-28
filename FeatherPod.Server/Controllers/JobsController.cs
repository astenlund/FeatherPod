using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly IJobService _jobService;

    public JobsController(IJobService jobService)
    {
        _jobService = jobService;
    }

    /// <summary>
    /// Get the status of a normalization job.
    /// </summary>
    [HttpGet("{jobId}")]
    [ProducesResponseType<JobStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatus(string jobId)
    {
        var entity = await _jobService.GetJobStatusAsync(jobId, HttpContext.RequestAborted);
        if (entity == null)
        {
            return NotFound(new { error = $"Job '{jobId}' not found" });
        }

        return Ok(JobStatusResponse.FromEntity(entity));
    }
}
