namespace FeatherPod.Server.Services;

/// <summary>
/// A move or copy refused because it would collide with another writer of the same file name in the target feed. Nothing was
/// published; the API reports it as 409 Conflict.
/// </summary>
public sealed class EpisodeConflictException(EpisodeConflictReason reason, string message) : InvalidOperationException(message)
{
    public EpisodeConflictReason Reason { get; } = reason;
}

public enum EpisodeConflictReason
{
    /// <summary>
    /// The target feed has an episode with the file name (found before the copy or at the commit).
    /// </summary>
    TargetHasEpisode,

    /// <summary>
    /// The target feed's audio path for the file name already holds a blob, which the create-only copy never overwrites.
    /// </summary>
    TargetPathTaken
}
