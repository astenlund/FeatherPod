using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Converts audio to 16 kHz mono WAV with FFMpegCore, the input format the Azure Speech
/// endpoints accept for containers they reject (M4A/M4B/MP4).
/// </summary>
public sealed class WavConverter
{
    private readonly FFmpegBinaryManager _binaryManager;
    private readonly ILogger<WavConverter> _logger;

    public WavConverter(FFmpegBinaryManager binaryManager, ILogger<WavConverter> logger)
    {
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <summary>
    /// Writes <paramref name="inputPath"/> as 16 kHz mono WAV to <paramref name="wavPath"/>,
    /// overwriting it. On failure or cancellation the output is deleted before the exception
    /// propagates, so callers can treat a thrown conversion as leaving no output behind.
    /// </summary>
    public async Task ConvertAsync(string inputPath, string wavPath, CancellationToken ct)
    {
        if (!await _binaryManager.EnsureFFmpegAvailableAsync(ct))
        {
            throw new InvalidOperationException("FFmpeg is not available for WAV conversion");
        }

        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(wavPath, overwrite: true, options => options
                    .WithAudioSamplingRate(16000)
                    .WithCustomArgument("-ac 1")
                    .ForceFormat("wav"))
                .CancellableThrough(ct)
                .WithLogLevel(FFMpegLogLevel.Error)
                .ProcessAsynchronously();
        }
        catch
        {
            FileHelper.TryDeleteFile(wavPath, _logger);
            throw;
        }
    }
}
