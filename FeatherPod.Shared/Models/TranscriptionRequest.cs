namespace FeatherPod.Shared.Models;

/// <summary>
/// Request to transcribe an uploaded audio file.
/// Submitted to the in-memory transcription channel.
/// </summary>
public record TranscriptionRequest
{
    required public string JobId { get; init; }

    required public string FeedId { get; init; }

    required public string FileName { get; init; }

    required public string EpisodeId { get; init; }

    /// <summary>
    /// The job attempt this run was started for (null for the first attempt). A run whose attempt is no longer the job's current one
    /// records nothing.
    /// </summary>
    public int? Attempt { get; init; }

    /// <summary>
    /// Whether the run reads the published episode audio (a transcript retry) instead of the job's pending upload blob.
    /// </summary>
    public bool FromEpisodeAudio { get; init; }
}
