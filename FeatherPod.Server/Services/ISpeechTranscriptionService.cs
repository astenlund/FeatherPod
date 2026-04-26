namespace FeatherPod.Server.Services;

/// <summary>
/// Abstraction over the Azure Speech transcription REST APIs.
/// Exposes both the synchronous Fast Transcription endpoint (preferred)
/// and the batch endpoint (fallback for audio &gt; ~110 minutes).
/// </summary>
public interface ISpeechTranscriptionService
{
    /// <summary>
    /// Whether transcription is available. Implementations may return <c>false</c> when
    /// the underlying service is not configured.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Submit audio to the synchronous Fast Transcription endpoint and return VTT.
    /// Returns <c>null</c> if the response contains no recognized phrases.
    /// Throws <see cref="FastTranscriptionUnavailableException"/> when the audio is too
    /// long/large for Fast or the endpoint is unavailable in the region; the caller
    /// should fall back to the batch path. The stream is read once and not seeked.
    /// </summary>
    Task<string?> TranscribeFastAsync(Stream audio, string contentType, string? fileName, CancellationToken ct);

    /// <summary>
    /// Submit a batch transcription job. Returns the self link (transcription URL).
    /// </summary>
    Task<string> SubmitAsync(string audioUrl, CancellationToken ct);

    /// <summary>
    /// Poll until the transcription reaches a terminal state. Returns <c>(status, filesListUrl, errorMessage)</c>.
    /// </summary>
    Task<(string Status, string? FilesListUrl, string? ErrorMessage)> PollUntilCompleteAsync(string transcriptionUrl, CancellationToken ct);

    /// <summary>
    /// Download the batch result and convert to VTT with speaker diarization.
    /// Returns <c>null</c> if no recognized phrases are found.
    /// </summary>
    Task<string?> GetResultAsVttAsync(string filesListUrl, CancellationToken ct);

    /// <summary>
    /// Delete the batch transcription job from Azure (quota cleanup).
    /// </summary>
    Task DeleteAsync(string transcriptionUrl, CancellationToken ct);
}
