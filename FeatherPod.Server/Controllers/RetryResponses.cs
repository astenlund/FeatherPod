using FeatherPod.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace FeatherPod.Server.Controllers;

/// <summary>
/// HTTP mapping of <see cref="JobRetryResult"/> shared by the transcript and normalization retry endpoints: 202 with the job that now
/// runs, or a refusal carrying a stable machine-readable <c>reason</c> beside its human-readable <c>error</c>.
/// </summary>
internal static class RetryResponses
{
    /// <summary>The <c>reason</c> values of retry refusals, one per <see cref="JobRetryOutcome"/> refusal.</summary>
    internal static class Reasons
    {
        public const string NotFound = "not-found";
        public const string NotRetryable = "not-retryable";
        public const string SpeechUnavailable = "speech-unavailable";
        public const string AudioMissing = "audio-missing";
        public const string RetryActive = "retry-active";
        public const string SourceGone = "source-gone";
        public const string Conflict = "conflict";
        public const string QueueFailed = "queue-failed";
    }

    public static IActionResult ToActionResult(ControllerBase controller, JobRetryResult result) => result.Outcome switch
    {
        JobRetryOutcome.Accepted when result.Job is { } job => controller.Accepted($"/api/jobs/{job.JobId}", job),
        JobRetryOutcome.Accepted => controller.StatusCode(StatusCodes.Status500InternalServerError, new { error = "The retry started but its job could not be read" }),
        JobRetryOutcome.NotFound => controller.NotFound(new { error = "Not found", reason = Reasons.NotFound }),
        JobRetryOutcome.NotRetryable => controller.Conflict(new { error = "Nothing to retry: only a failed transcript or a failed normalization can be retried", reason = Reasons.NotRetryable }),
        JobRetryOutcome.SpeechUnavailable => controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Transcription is not configured on this server", reason = Reasons.SpeechUnavailable }),
        JobRetryOutcome.AudioMissing => controller.Conflict(new { error = "The episode's audio is missing", reason = Reasons.AudioMissing }),
        JobRetryOutcome.RetryActive => controller.Conflict(new { error = "A transcript retry of this episode is already running", reason = Reasons.RetryActive }),
        JobRetryOutcome.SourceGone => controller.StatusCode(StatusCodes.Status410Gone, new { error = "The uploaded source is gone; upload the file again", reason = Reasons.SourceGone }),
        JobRetryOutcome.Conflict => controller.Conflict(new { error = "The job changed while it was being retried", reason = Reasons.Conflict }),
        JobRetryOutcome.QueueFailed => controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The retry could not be queued; try again", reason = Reasons.QueueFailed }),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unknown retry outcome"),
    };
}
