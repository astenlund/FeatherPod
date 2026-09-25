using FeatherPod.Shared.Services;

namespace FeatherPod.Tests;

/// <summary>
/// xUnit Fact that skips when no FFmpeg is installed locally or on PATH. Checks availability only
/// and never downloads, so a test run on a machine without FFmpeg stays offline.
/// </summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _isFFmpegAvailable = new(() => new FFmpegBinaryManager().IsFFmpegAvailable());

    public FFmpegFactAttribute()
    {
        if (!_isFFmpegAvailable.Value)
        {
            Skip = $"FFmpeg is not installed in {FFmpegBinaryManager.GetBinaryDirectory()} or on PATH.";
        }
    }
}
