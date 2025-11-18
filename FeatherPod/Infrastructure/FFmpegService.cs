using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Service for FFmpeg-based audio processing operations.
/// </summary>
internal static class FFmpegService
{
    private static bool? _isFFmpegAvailable;

    /// <summary>
    /// Check if FFmpeg is installed and available on the system PATH.
    /// </summary>
    public static bool IsFFmpegAvailable()
    {
        if (_isFFmpegAvailable.HasValue)
            return _isFFmpegAvailable.Value;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();
            _isFFmpegAvailable = process.ExitCode == 0;
        }
        catch
        {
            _isFFmpegAvailable = false;
        }

        return _isFFmpegAvailable.Value;
    }

    /// <summary>
    /// Normalize audio file to target loudness using FFmpeg's loudnorm filter.
    /// Uses two-pass processing for accurate normalization.
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

        try
        {
            // Pass 1: Analyze loudness
            var analysisResult = await AnalyzeLoudnessAsync(inputPath, config);
            if (analysisResult == null)
            {
                return null;
            }

            // Pass 2: Apply normalization with measured values
            var success = await ApplyNormalizationAsync(
                inputPath,
                tempFile,
                config,
                analysisResult);

            if (success)
            {
                AnsiConsole.MarkupLine($"[grey]Original: {analysisResult.input_i} LUFS → Normalized: {config.TargetLoudness} LUFS[/]");
            }

            return success ? tempFile : null;
        }
        catch
        {
            // Clean up temp file on failure
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { /* Ignore cleanup errors */ }
            }
            return null;
        }
    }

    /// <summary>
    /// Pass 1: Analyze audio loudness and return measured values.
    /// </summary>
    private static async Task<LoudnormAnalysisResult?> AnalyzeLoudnessAsync(
        string inputPath,
        AudioNormalizationConfig config)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        var targetLoudness = config.TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeak = config.TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRange = config.LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{inputPath}\" -af loudnorm=I={targetLoudness}:TP={truePeak}:LRA={loudnessRange}:print_format=json -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        // Parse JSON output from stderr (FFmpeg writes to stderr)
        var errorText = error.ToString();
        var jsonMatch = Regex.Match(errorText, @"\{[^\}]*""input_i""[^\}]*\}", RegexOptions.Singleline);

        if (!jsonMatch.Success)
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<LoudnormAnalysisResult>(jsonMatch.Value);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pass 2: Apply normalization using measured values from Pass 1.
    /// </summary>
    private static async Task<bool> ApplyNormalizationAsync(
        string inputPath,
        string outputPath,
        AudioNormalizationConfig config,
        LoudnormAnalysisResult analysis)
    {
        var targetLoudness = config.TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeak = config.TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRange = config.LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        var loudnormFilter = $"loudnorm=I={targetLoudness}:TP={truePeak}:LRA={loudnessRange}:" +
                           $"measured_I={analysis.input_i}:measured_TP={analysis.input_tp}:" +
                           $"measured_LRA={analysis.input_lra}:measured_thresh={analysis.input_thresh}:" +
                           $"offset={analysis.target_offset}:print_format=summary";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{inputPath}\" -af {loudnormFilter} -ar 48000 \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var duration = await GetAudioDurationAsync(inputPath);
        var progressBar = duration > 0
            ? AnsiConsole.Progress().Start(ctx =>
            {
                var task = ctx.AddTask("[cyan]Normalizing audio...[/]", maxValue: duration);

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        var timeMatch = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+\.\d+)");
                        if (timeMatch.Success)
                        {
                            var hours = double.Parse(timeMatch.Groups[1].Value);
                            var minutes = double.Parse(timeMatch.Groups[2].Value);
                            var seconds = double.Parse(timeMatch.Groups[3].Value);
                            var currentTime = hours * 3600 + minutes * 60 + seconds;
                            task.Value = currentTime;
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                task.StopTask();
                return process.ExitCode;
            })
            : await RunProcessWithSpinnerAsync(process, "Normalizing audio...");

        return progressBar == 0 && File.Exists(outputPath);
    }

    /// <summary>
    /// Get audio duration in seconds using FFprobe.
    /// </summary>
    private static async Task<double> GetAudioDurationAsync(string inputPath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), out var duration))
            {
                return duration;
            }
        }
        catch
        {
            // Fallback to -1 if ffprobe fails
        }

        return -1;
    }

    /// <summary>
    /// Run a process with a spinner (fallback when progress bar fails).
    /// </summary>
    private static async Task<int> RunProcessWithSpinnerAsync(Process process, string statusMessage)
    {
        return await AnsiConsole.Status()
            .StartAsync(statusMessage, async _ =>
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                return process.ExitCode;
            });
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
            // TagLib failed, continue to ffprobe
        }

        try
        {
            // Try ffprobe to get creation_time from container metadata
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v quiet -show_entries format_tags=creation_time -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
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
            // ffprobe failed, return null
        }

        return null;
    }

    /// <summary>
    /// Result from FFmpeg loudnorm analysis (Pass 1).
    /// </summary>
    private class LoudnormAnalysisResult
    {
        public string input_i { get; set; } = string.Empty;
        public string input_tp { get; set; } = string.Empty;
        public string input_lra { get; set; } = string.Empty;
        public string input_thresh { get; set; } = string.Empty;
        public string target_offset { get; set; } = string.Empty;
    }
}
