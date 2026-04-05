namespace FeatherPod.Shared.Models;

/// <summary>
/// String constants for the TranscriptionStatus field on JobStatusEntity.
/// Stored as strings in Table Storage (not an enum) for forward compatibility.
/// </summary>
public static class TranscriptionStatuses
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
