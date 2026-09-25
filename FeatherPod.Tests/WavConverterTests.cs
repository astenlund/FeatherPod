using FeatherPod.Shared.Services;
using FFMpegCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

public sealed class WavConverterTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FeatherPod.Tests", $"wav-{Guid.NewGuid():N}");
    private readonly FFmpegBinaryManager _binaryManager = new();
    private readonly WavConverter _converter;

    public WavConverterTests()
    {
        _converter = new WavConverter(_binaryManager, NullLogger<WavConverter>.Instance);
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: a failed test can leave ffmpeg holding a file, and throwing here
            // would mask that test's real failure.
        }
    }

    [FFmpegFact]
    public async Task ConvertAsync_WritesSixteenKilohertzMonoWav()
    {
        // Arrange
        var input = await GenerateSineAsync("short.m4a", seconds: 2, "-ac 2 -ar 44100 -c:a aac");
        var output = Path.Combine(_workDir, "short.wav");

        // Act
        await _converter.ConvertAsync(input, output, CancellationToken.None);

        // Assert
        var analysis = await FFProbe.AnalyseAsync(output);
        Assert.Equal("wav", analysis.Format.FormatName);
        Assert.Equal(16000, analysis.PrimaryAudioStream!.SampleRateHz);
        Assert.Equal(1, analysis.PrimaryAudioStream.Channels);
    }

    [FFmpegFact]
    public async Task ConvertAsync_Cancelled_DeletesPartialOutput()
    {
        // Arrange
        var input = await GenerateLongConcatInputAsync(hours: 10);
        var output = Path.Combine(_workDir, "long-16k.wav");
        using var cts = new CancellationTokenSource();
        var conversion = _converter.ConvertAsync(input, output, cts.Token);
        await WaitForFileAsync(output);

        // Act
        await cts.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion.WaitAsync(WaitLimit));
        Assert.False(File.Exists(output));
    }

    [FFmpegFact]
    public async Task ConvertAsync_InvalidInput_LeavesNoOutput()
    {
        // Arrange
        var input = Path.Combine(_workDir, "garbage.m4a");
        await File.WriteAllTextAsync(input, "not audio");
        var output = Path.Combine(_workDir, "garbage.wav");
        await File.WriteAllTextAsync(output, "stale output from an earlier run");

        // Act
        var exception = await Record.ExceptionAsync(() => _converter.ConvertAsync(input, output, CancellationToken.None));

        // Assert
        Assert.NotNull(exception);
        Assert.False(File.Exists(output));
    }

    /// <summary>
    /// Builds an ffconcat playlist that repeats one short segment for <paramref name="hours"/>, so the
    /// conversion runs far longer than the test needs to cancel it, without paying to generate that much audio.
    /// </summary>
    private async Task<string> GenerateLongConcatInputAsync(int hours)
    {
        const int SegmentSeconds = 60;
        var segment = await GenerateSineAsync("segment.wav", SegmentSeconds, "-ac 1 -ar 8000");
        var repeats = Enumerable.Repeat($"file '{Path.GetFileName(segment)}'", hours * 3600 / SegmentSeconds);
        var playlist = Path.Combine(_workDir, "long.ffconcat");
        await File.WriteAllLinesAsync(playlist, ["ffconcat version 1.0", .. repeats]);

        return playlist;
    }

    private async Task<string> GenerateSineAsync(string fileName, int seconds, string outputArguments)
    {
        // Resolve FFmpeg here rather than relying on [FFmpegFact] having configured FFMpegCore in this process.
        Assert.True(await _binaryManager.EnsureFFmpegAvailableAsync());

        var path = Path.Combine(_workDir, fileName);
        await FFMpegArguments
            .FromFileInput($"sine=frequency=440:duration={seconds}", verifyExists: false, options => options.ForceFormat("lavfi"))
            .OutputToFile(path, overwrite: true, options => options.WithCustomArgument(outputArguments))
            .ProcessAsynchronously();

        return path;
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(WaitLimit);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }
}
