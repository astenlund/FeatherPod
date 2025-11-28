using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Shared.Models;
using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Audio normalization service using FFmpeg's loudnorm filter.
/// Uses two-pass EBU R128 normalization for consistent podcast loudness.
/// </summary>
public partial class AudioNormalizationService : IAudioNormalizationService
{
    private readonly FFmpegBinaryManager _binaryManager;
    private readonly ILogger<AudioNormalizationService> _logger;

    // Podcast standard loudness settings (EBU R128)
    private const double TargetLoudness = -16.0;  // LUFS
    private const double TruePeak = -1.5;         // dBTP
    private const double LoudnessRange = 11.0;    // LRA

    // Timeout for Pass 1 analysis (protects against corrupted/extremely long files)
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(10);

    public AudioNormalizationService(FFmpegBinaryManager binaryManager, ILogger<AudioNormalizationService> logger)
    {
        _binaryManager = binaryManager;
        _logger = logger;
    }

    /// <summary>
    /// Check if FFmpeg is available for normalization.
    /// </summary>
    public bool IsFFmpegAvailable()
    {
        return _binaryManager.IsFFmpegAvailable();
    }

    /// <summary>
    /// Ensure FFmpeg is available, downloading if necessary.
    /// </summary>
    public Task<bool> EnsureFFmpegAvailableAsync()
    {
        return _binaryManager.EnsureFFmpegAvailableAsync();
    }

    /// <summary>
    /// Normalize audio file to podcast standard loudness (-16 LUFS).
    /// Uses two-pass processing for accurate EBU R128 normalization.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the normalized temporary file, or null if normalization fails</returns>
    public async Task<string?> NormalizeAudioAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        if (!await EnsureFFmpegAvailableAsync())
        {
            _logger.LogError("FFmpeg is not available for audio normalization");

            return null;
        }

        var fileName = Path.GetFileName(inputPath);
        _logger.LogInformation("Starting audio normalization for {FileName}", fileName);

        // Create temp file for normalized audio
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);

        var extension = Path.GetExtension(inputPath);
        var tempFile = Path.Combine(tempDir, $"{Guid.NewGuid()}{extension}");

        try
        {
            // Pass 1: Analyze loudness
            _logger.LogDebug("Pass 1: Analyzing loudness for {FileName}", fileName);
            var analysis = await AnalyzeLoudnessAsync(inputPath, cancellationToken);

            if (analysis == null)
            {
                _logger.LogError("Loudness analysis failed for {FileName}", fileName);

                return null;
            }

            _logger.LogDebug("Analysis complete: Input={InputLufs} LUFS, Target={TargetLufs} LUFS",
                analysis.InputI, TargetLoudness);

            // Pass 2: Apply normalization with measured values
            _logger.LogDebug("Pass 2: Applying normalization for {FileName}", fileName);
            var success = await ApplyNormalizationAsync(inputPath, tempFile, analysis, cancellationToken);

            if (!success)
            {
                _logger.LogError("Normalization failed for {FileName}", fileName);
                CleanupTempFile(tempFile);

                return null;
            }

            _logger.LogInformation("Audio normalization complete for {FileName}: {InputLufs} LUFS -> {TargetLufs} LUFS",
                fileName, analysis.InputI, TargetLoudness);

            return tempFile;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Audio normalization cancelled for {FileName}", fileName);
            CleanupTempFile(tempFile);

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio normalization failed for {FileName}", fileName);
            CleanupTempFile(tempFile);

            return null;
        }
    }

    /// <summary>
    /// Pass 1: Analyze audio loudness using FFmpeg's loudnorm filter.
    /// </summary>
    private async Task<LoudnessAnalysis?> AnalyzeLoudnessAsync(string inputPath, CancellationToken cancellationToken)
    {
        var error = new StringBuilder();

        var targetLoudnessStr = TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeakStr = TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRangeStr = LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        var ffmpegPath = GetFFmpegPath();

        using var process = new Process();
        process.StartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{inputPath}\" -af loudnorm=I={targetLoudnessStr}:TP={truePeakStr}:LRA={loudnessRangeStr}:print_format=json -f null -",
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
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Use linked token with timeout to protect against corrupted/extremely long files
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AnalysisTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill the FFmpeg process to avoid orphaned processes
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

        // Parse JSON output from stderr
        var errorText = error.ToString();
        var jsonMatch = JsonRegex().Match(errorText);

        if (!jsonMatch.Success)
        {
            _logger.LogWarning("Could not parse loudnorm JSON output");

            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoudnessAnalysis>(jsonMatch.Value);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize loudnorm JSON");

            return null;
        }
    }

    /// <summary>
    /// Pass 2: Apply normalization using FFMpegCore with measured values.
    /// </summary>
    private async Task<bool> ApplyNormalizationAsync(string inputPath, string outputPath, LoudnessAnalysis analysis, CancellationToken cancellationToken)
    {
        var loudnormFilter = BuildLoudnormFilter(analysis);

        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, true, options => options
                    .WithCustomArgument($"-af {loudnormFilter}")
                    .WithAudioSamplingRate())
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously();

            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFMpegCore processing failed");

            return false;
        }
    }

    /// <summary>
    /// Build the loudnorm filter string with measured values from Pass 1.
    /// </summary>
    private static string BuildLoudnormFilter(LoudnessAnalysis analysis)
    {
        var targetLoudnessStr = TargetLoudness.ToString("G", CultureInfo.InvariantCulture);
        var truePeakStr = TruePeak.ToString("G", CultureInfo.InvariantCulture);
        var loudnessRangeStr = LoudnessRange.ToString("G", CultureInfo.InvariantCulture);

        return $"loudnorm=I={targetLoudnessStr}:TP={truePeakStr}:LRA={loudnessRangeStr}:" +
               $"measured_I={analysis.InputI}:measured_TP={analysis.InputTp}:" +
               $"measured_LRA={analysis.InputLra}:measured_thresh={analysis.InputThresh}:" +
               $"offset={analysis.TargetOffset}:print_format=summary";
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

    private void CleanupTempFile(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temp file: {Path}", path);
            }
        }
    }

    [GeneratedRegex("""\{[^{}]*"input_i"[^{}]*\}""", RegexOptions.Singleline)]
    private static partial Regex JsonRegex();
}
