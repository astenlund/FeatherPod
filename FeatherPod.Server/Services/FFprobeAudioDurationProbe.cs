using System.Diagnostics;
using System.Globalization;
using FeatherPod.Shared;
using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

/// <summary>
/// <see cref="IAudioDurationProbe"/> backed by ffprobe (shipped with ffmpeg via
/// <see cref="FFmpegBinaryManager"/>). Runs <c>ffprobe -show_entries format=duration</c>
/// and parses a <see cref="double"/> seconds value.
/// </summary>
public sealed class FFprobeAudioDurationProbe : IAudioDurationProbe
{
    private readonly ILogger<FFprobeAudioDurationProbe> _logger;

    public FFprobeAudioDurationProbe(ILogger<FFprobeAudioDurationProbe> logger)
    {
        _logger = logger;
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct)
    {
        var ffprobePath = FFmpegBinaryManager.GetFFprobePath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", filePath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe failed for {filePath} (exit {process.ExitCode}): {stderr.Truncate(500)}");
        }

        if (!double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            _logger.LogWarning("ffprobe produced unparseable duration {Stdout} for {FilePath}", stdout, filePath);

            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
