using FeatherPod.Shared.Models;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Interface for audio normalization services.
/// </summary>
public interface IAudioNormalizationService
{
    /// <summary>
    /// Check if FFmpeg is available for normalization.
    /// </summary>
    bool IsFFmpegAvailable();

    /// <summary>
    /// Ensure FFmpeg is available, downloading if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if FFmpeg is available after this call.</returns>
    Task<bool> EnsureFFmpegAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalize audio file to podcast standard loudness (-16 LUFS).
    /// Uses two-pass processing for accurate EBU R128 normalization.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="progressCallback">Optional callback for progress updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the normalized temporary file, or null if normalization fails</returns>
    Task<string?> NormalizeAudioAsync(
        string inputPath,
        Action<ProgressUpdate>? progressCallback = null,
        CancellationToken cancellationToken = default);
}
