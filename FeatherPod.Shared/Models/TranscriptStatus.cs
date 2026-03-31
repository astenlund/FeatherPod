namespace FeatherPod.Shared.Models;

/// <summary>
/// Status of transcript generation for an episode.
/// </summary>
public enum TranscriptStatus
{
    /// <summary>
    /// Transcript generated and available in blob storage.
    /// </summary>
    Available,

    /// <summary>
    /// Transcription failed; episode published without transcript.
    /// </summary>
    Failed
}
