namespace FeatherPod.Server.Services;

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
    Task<bool> EnsureFFmpegAvailableAsync();

    /// <summary>
    /// Normalize audio file to podcast standard loudness (-16 LUFS).
    /// Uses two-pass processing for accurate EBU R128 normalization.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the normalized temporary file, or null if normalization fails</returns>
    Task<string?> NormalizeAudioAsync(string inputPath, CancellationToken cancellationToken = default);
}
