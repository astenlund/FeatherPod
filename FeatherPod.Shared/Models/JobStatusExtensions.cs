using System.Diagnostics.CodeAnalysis;

namespace FeatherPod.Shared.Models;

/// <summary>
/// Extension methods for <see cref="JobStatus"/> and its string representation.
/// </summary>
public static class JobStatusExtensions
{
    /// <summary>
    /// Returns true if the status is terminal (Completed, Failed, or Cancelled).
    /// </summary>
    public static bool IsTerminal(this JobStatus status)
    {
        return status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
    }

    /// <summary>
    /// Returns true if the status string matches a terminal <see cref="JobStatus"/>
    /// (Completed, Failed, or Cancelled). Used by entities and DTOs that store the status as a string.
    /// </summary>
    public static bool IsTerminal([NotNullWhen(true)] this string? status)
    {
        return status is nameof(JobStatus.Completed) or nameof(JobStatus.Failed) or nameof(JobStatus.Cancelled);
    }
}
