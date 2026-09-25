using FFMpegCore;

namespace FeatherPod.Shared.Services;

/// <summary>
/// <see cref="IAudioDurationProbe"/> backed by FFMpegCore's <see cref="FFProbe"/>. Ensures the
/// ffmpeg/ffprobe binaries through <see cref="FFmpegBinaryManager"/> first, which also points
/// FFMpegCore at a locally downloaded copy.
/// </summary>
public sealed class FFprobeAudioDurationProbe : IAudioDurationProbe
{
    private readonly FFmpegBinaryManager _binaryManager;

    public FFprobeAudioDurationProbe(FFmpegBinaryManager binaryManager)
    {
        _binaryManager = binaryManager;
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct)
    {
        if (!await _binaryManager.EnsureFFmpegAvailableAsync(ct))
        {
            throw new InvalidOperationException("FFmpeg is not available for probing audio duration");
        }

        var analysis = await FFProbe.AnalyseAsync(filePath, cancellationToken: ct);

        // FFMpegCore reports a missing or unparseable duration as zero, which would silently
        // route the file down the Fast path; treat it as a probe failure instead.
        if (analysis.Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"ffprobe reported no duration for {Path.GetFileName(filePath)}");
        }

        return analysis.Duration;
    }
}
