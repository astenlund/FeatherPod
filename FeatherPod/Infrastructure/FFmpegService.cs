using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FFMpegCore;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Service for FFmpeg-based audio processing operations.
/// Uses FFMpegCore library with auto-download support via FFmpegBinaryManager.
/// </summary>
internal static partial class FFmpegService
{
    private static readonly Regex ProgressRegex = MyRegex();
    private static readonly FFmpegBinaryManager BinaryManager = new();

    /// <summary>
    /// Check if FFmpeg is installed and available (on PATH or downloaded locally).
    /// </summary>
    public static bool IsFFmpegAvailable()
    {
        return BinaryManager.IsFFmpegAvailable();
    }

    /// <summary>
    /// Download FFmpeg binaries to the local directory.
    /// </summary>
    public static Task<bool> DownloadFFmpegAsync()
    {
        return BinaryManager.DownloadFFmpegAsync();
    }

    /// <summary>
    /// Normalize audio file to target loudness using FFmpeg's loudnorm filter.
    /// Uses two-pass processing for accurate EBU R128 normalization.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="config">Normalization configuration</param>
    /// <returns>Path to the normalized temporary file, or null if normalization fails</returns>
    public static async Task<string?> NormalizeAudioAsync(string inputPath, AudioNormalizationConfig config)
    {
        if (!IsFFmpegAvailable())
        {
            return null;
        }

        // Create temp file for normalized audio
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);

        var extension = Path.GetExtension(inputPath);
        var tempFile = Path.Combine(tempDir, $"{Guid.NewGuid()}{extension}");

        // Get duration first for progress tracking
        var duration = await GetAudioDurationAsync(inputPath);

        try
        {
            if (duration > TimeSpan.Zero)
            {
                // Use two progress bars when duration is known
                return await NormalizeWithProgressAsync(inputPath, tempFile, config, duration);
            }
            else
            {
                // Fall back to spinner when duration unknown
                return await NormalizeWithSpinnerAsync(inputPath, tempFile, config);
            }
        }
        catch (Exception)
        {
            // Clean up temp file on failure
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); }
                catch { /* Ignore cleanup errors */ }
            }
            return null;
        }
    }

    /// <summary>
    /// Normalize with two progress bars (when duration is known).
    /// </summary>
    private static async Task<string?> NormalizeWithProgressAsync(
        string inputPath,
        string outputPath,
        AudioNormalizationConfig config,
        TimeSpan duration)
    {
        return await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn())
            .StartAsync(async ctx =>
            {
                var analyzeTask = ctx.AddTask("[cyan]Analyzing audio...[/]", maxValue: 100);
                var normalizeTask = ctx.AddTask("[grey]Normalizing audio...[/]", maxValue: 100);

                // Pass 1: Analyze with progress
                var analysis = await AnalyzeLoudnessWithProgressAsync(inputPath, config, duration, analyzeTask);
                analyzeTask.Value = 100;
                analyzeTask.StopTask();

                if (analysis == null)
                {
                    AnsiConsole.MarkupLine("[red]Audio analysis failed[/]");
                    normalizeTask.StopTask();
                    return null;
                }

                // Update normalize task to active state
                normalizeTask.Description = "[cyan]Normalizing audio...[/]";

                // Pass 2: Apply with progress
                var success = await ApplyNormalizationWithProgressAsync(inputPath, outputPath, config, analysis, duration, normalizeTask);
                normalizeTask.Value = 100;
                normalizeTask.StopTask();

                if (success)
                {
                    AnsiConsole.MarkupLine($"[grey]Original: {analysis.InputI} LUFS → Normalized: {config.TargetLoudness} LUFS[/]");
                    return outputPath;
                }

                return null;
            });
    }

    /// <summary>
    /// Normalize with spinner (when duration is unknown).
    /// </summary>
    private static async Task<string?> NormalizeWithSpinnerAsync(
        string inputPath,
        string outputPath,
        AudioNormalizationConfig config)
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cyan]Analyzing and normalizing audio...[/]", async _ =>
            {
                // Pass 1: Analyze
                var analysis = await AnalyzeLoudnessAsync(inputPath, config);
                if (analysis == null)
                {
                    return null;
                }

                // Pass 2: Apply
                var success = await ApplyNormalizationAsync(inputPath, outputPath, config, analysis);

                if (success)
                {
                    AnsiConsole.MarkupLine($"[grey]Original: {analysis.InputI} LUFS → Normalized: {config.TargetLoudness} LUFS[/]");
                    return outputPath;
                }

                return null;
            });
    }

    /// <summary>
    /// Pass 1: Analyze audio loudness with progress tracking.
    /// </summary>
    private static async Task<LoudnessAnalysis?> AnalyzeLoudnessWithProgressAsync(
        string inputPath,
        AudioNormalizationConfig config,
        TimeSpan duration,
        ProgressTask progressTask)
    {
        var error = new StringBuilder();
        var durationSeconds = duration.TotalSeconds;

        var targetLoudness = config.TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeak = config.TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRange = config.LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        var ffmpegPath = GetFFmpegPath();

        using var process = new Process();
        process.StartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{inputPath}\" -af loudnorm=I={targetLoudness}:TP={truePeak}:LRA={loudnessRange}:print_format=json -f null -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                error.AppendLine(e.Data);

                // Parse progress from stderr
                var match = ProgressRegex.Match(e.Data);
                if (match.Success)
                {
                    var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                    var currentTime = hours * 3600 + minutes * 60 + seconds;
                    var percent = Math.Min(100, (currentTime / durationSeconds) * 100);
                    progressTask.Value = percent;

                    var position = NormalizationProgressHelper.FormatPosition(TimeSpan.FromSeconds(currentTime), duration);
                    progressTask.Description = $"[cyan]Analyzing[/] [grey]{position}[/]";
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        // Parse JSON output from stderr
        var errorText = error.ToString();
        var jsonMatch = JsonRegex().Match(errorText);

        if (!jsonMatch.Success)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoudnessAnalysis>(jsonMatch.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pass 1: Analyze audio loudness (no progress, for spinner mode).
    /// </summary>
    private static async Task<LoudnessAnalysis?> AnalyzeLoudnessAsync(
        string inputPath,
        AudioNormalizationConfig config)
    {
        var error = new StringBuilder();

        var targetLoudness = config.TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeak = config.TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRange = config.LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        var ffmpegPath = GetFFmpegPath();

        using var process = new Process();
        process.StartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{inputPath}\" -af loudnorm=I={targetLoudness}:TP={truePeak}:LRA={loudnessRange}:print_format=json -f null -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        var errorText = error.ToString();
        var jsonMatch = JsonRegex().Match(errorText);

        if (!jsonMatch.Success)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoudnessAnalysis>(jsonMatch.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pass 2: Apply normalization with progress tracking.
    /// </summary>
    private static async Task<bool> ApplyNormalizationWithProgressAsync(
        string inputPath,
        string outputPath,
        AudioNormalizationConfig config,
        LoudnessAnalysis analysis,
        TimeSpan duration,
        ProgressTask progressTask)
    {
        var loudnormFilter = BuildLoudnormFilter(config, analysis);

        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, true, options => options
                    .WithCustomArgument($"-af {loudnormFilter}")
                    .WithAudioSamplingRate())
                .NotifyOnProgress(percent =>
                {
                    progressTask.Value = percent;
                    var currentPosition = TimeSpan.FromSeconds(duration.TotalSeconds * percent / 100);
                    var position = NormalizationProgressHelper.FormatPosition(currentPosition, duration);
                    progressTask.Description = $"[cyan]Normalizing[/] [grey]{position}[/]";
                }, duration)
                .ProcessAsynchronously();

            return File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pass 2: Apply normalization (no progress, for spinner mode).
    /// </summary>
    private static async Task<bool> ApplyNormalizationAsync(
        string inputPath,
        string outputPath,
        AudioNormalizationConfig config,
        LoudnessAnalysis analysis)
    {
        var loudnormFilter = BuildLoudnormFilter(config, analysis);

        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, true, options => options
                    .WithCustomArgument($"-af {loudnormFilter}")
                    .WithAudioSamplingRate())
                .ProcessAsynchronously();

            return File.Exists(outputPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Build the loudnorm filter string with measured values.
    /// </summary>
    private static string BuildLoudnormFilter(AudioNormalizationConfig config, LoudnessAnalysis analysis)
    {
        var targetLoudness = config.TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeak = config.TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRange = config.LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        return $"loudnorm=I={targetLoudness}:TP={truePeak}:LRA={loudnessRange}:" +
               $"measured_I={analysis.InputI}:measured_TP={analysis.InputTp}:" +
               $"measured_LRA={analysis.InputLra}:measured_thresh={analysis.InputThresh}:" +
               $"offset={analysis.TargetOffset}:print_format=summary";
    }

    /// <summary>
    /// Get audio duration using FFMpegCore's FFProbe.
    /// </summary>
    private static async Task<TimeSpan> GetAudioDurationAsync(string inputPath)
    {
        try
        {
            var mediaInfo = await FFProbe.AnalyseAsync(inputPath);
            return mediaInfo.Duration;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Extract creation time from audio file metadata using TagLib.
    /// Falls back to ffprobe if TagLib fails.
    /// </summary>
    public static DateTime? ExtractCreationTime(string filePath)
    {
        try
        {
            // Try TagLib first (works for most audio formats)
            using var file = TagLib.File.Create(filePath);
            if (file.Tag.DateTagged.HasValue)
            {
                return file.Tag.DateTagged.Value.ToUniversalTime();
            }
        }
        catch
        {
            // TagLib failed, continue to ffprobe fallback
        }

        try
        {
            // Try FFProbe to get creation_time from container metadata
            var ffprobePath = GetFFprobePath();

            using var process = new Process();
            process.StartInfo = new()
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -show_entries format_tags=creation_time -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output) && DateTime.TryParse(output, out var creationTime))
            {
                return creationTime.ToUniversalTime();
            }
        }
        catch
        {
            // FFprobe not available or failed
        }

        return null;
    }

    /// <summary>
    /// Get the path to the FFmpeg executable.
    /// </summary>
    private static string GetFFmpegPath()
    {
        var binDir = FFmpegBinaryManager.GetBinaryDirectory();
        var ffmpegName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var localPath = Path.Combine(binDir, ffmpegName);

        return File.Exists(localPath) ? localPath : "ffmpeg";
    }

    /// <summary>
    /// Get the path to the FFprobe executable.
    /// </summary>
    private static string GetFFprobePath()
    {
        var binDir = FFmpegBinaryManager.GetBinaryDirectory();
        var ffprobeName = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var localPath = Path.Combine(binDir, ffprobeName);

        return File.Exists(localPath) ? localPath : "ffprobe";
    }

    [GeneratedRegex("""time=(\d+):(\d+):(\d+\.\d+)""", RegexOptions.Compiled)]
    private static partial Regex MyRegex();

    [GeneratedRegex("""\{[^\}]*"input_i"[^\}]*\}""", RegexOptions.Singleline)]
    private static partial Regex JsonRegex();
}
