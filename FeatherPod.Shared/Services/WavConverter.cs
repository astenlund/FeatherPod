using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// <see cref="IWavConverter"/> backed by FFMpegCore, using the binaries <see cref="FFmpegBinaryManager"/> resolves.
/// </summary>
public sealed class WavConverter : IWavConverter
{
    private readonly FFmpegBinaryManager _binaryManager;
    private readonly ILogger<WavConverter> _logger;

    public WavConverter(FFmpegBinaryManager binaryManager, ILogger<WavConverter> logger)
    {
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <inheritdoc />
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
