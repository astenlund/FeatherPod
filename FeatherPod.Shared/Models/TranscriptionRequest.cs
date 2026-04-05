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
}
