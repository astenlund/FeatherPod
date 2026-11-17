namespace FeatherPod.Shared.Models;

/// <summary>
/// Values of <see cref="JobStatusEntity.Kind"/>, the single source of truth for what a job does. An upload job (normalization plus
/// transcription, creating an episode at its join) has no kind, so every job created before kinds existed is an upload job.
/// </summary>
public static class JobKinds
{
    /// <summary>
    /// A transcription-only job bound to an existing episode whose transcript failed. It reads the published episode audio, never a
    /// pending blob, and its join updates that episode's transcript status instead of creating an episode.
    /// </summary>
    public const string TranscriptRetry = "TranscriptRetry";

    /// <summary>
    /// Whether <paramref name="kind"/> names a transcript retry job.
    /// </summary>
    public static bool IsTranscriptRetry(string? kind) => string.Equals(kind, TranscriptRetry, StringComparison.Ordinal);
}
