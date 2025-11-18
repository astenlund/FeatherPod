namespace FeatherPod.Cli.Infrastructure;

/// <summary>
/// Configuration for FFmpeg-based audio loudness normalization.
/// </summary>
public class AudioNormalizationConfig
{
    /// <summary>
    /// Whether to enable audio normalization for uploads.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Target loudness in LUFS (Loudness Units relative to Full Scale).
    /// Industry standard for podcasts is -16 LUFS.
    /// </summary>
    public double TargetLoudness { get; set; } = -16.0;

    /// <summary>
    /// True peak limit in dBTP (prevents clipping during playback).
    /// </summary>
    public double TruePeak { get; set; } = -1.5;

    /// <summary>
    /// Loudness range target in LU (controls dynamic range).
    /// </summary>
    public double LoudnessRange { get; set; } = 11.0;
}
