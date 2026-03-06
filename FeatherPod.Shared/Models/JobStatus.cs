namespace FeatherPod.Shared.Models;

/// <summary>
/// Status of a normalization job.
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// Job is queued and waiting to be processed.
    /// </summary>
    Queued,

    /// <summary>
    /// Job is currently being processed by the Function.
    /// </summary>
    Processing,

    /// <summary>
    /// Job completed successfully. Episode has been created.
    /// </summary>
    Completed,

    /// <summary>
    /// Job failed. See Error property for details.
    /// </summary>
    Failed,

    /// <summary>
    /// Job was cancelled by the user.
    /// </summary>
    Cancelled
}
