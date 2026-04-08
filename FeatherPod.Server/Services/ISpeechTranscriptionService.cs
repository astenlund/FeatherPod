namespace FeatherPod.Server.Services;

/// <summary>
/// Abstraction over the Azure Speech batch transcription REST API (v3.2).
/// Lets callers and tests inject a fake for the HTTP-bound concrete implementation.
/// </summary>
public interface ISpeechTranscriptionService
{
    /// <summary>
    /// Whether transcription is available. Implementations may return <c>false</c> when
    /// the underlying service is not configured.
    /// </summary>
    bool IsAvailable { get; }

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
