using FeatherPod.Shared.Models;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Result of audio analysis (Pass 1).
/// </summary>
public record AudioAnalysisResult(LoudnessAnalysis Analysis, TimeSpan Duration);

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
    /// Get the duration of an audio file.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <returns>Duration of the audio, or TimeSpan.Zero if unable to determine</returns>
    Task<TimeSpan> GetAudioDurationAsync(string inputPath);

    /// <summary>
    /// Analyze audio file loudness (Pass 1 only).
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="progressCallback">Optional callback for progress updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Analysis result with loudness data and duration, or null if analysis fails</returns>
    Task<AudioAnalysisResult?> AnalyzeAudioAsync(
        string inputPath,
        Action<ProgressUpdate>? progressCallback = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply normalization using pre-computed analysis (Pass 2 only).
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="analysis">Loudness analysis from Pass 1</param>
    /// <param name="totalDuration">Total duration for progress calculation</param>
    /// <param name="progressCallback">Optional callback for progress updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the normalized temporary file, or null if normalization fails</returns>
    Task<string?> ApplyNormalizationAsync(
        string inputPath,
        LoudnessAnalysis analysis,
        TimeSpan totalDuration,
        Action<ProgressUpdate>? progressCallback = null,
        CancellationToken cancellationToken = default);

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
