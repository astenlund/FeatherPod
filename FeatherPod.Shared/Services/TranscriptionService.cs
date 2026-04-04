using System.Diagnostics;
using System.Globalization;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using FeatherPod.Shared.Models;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;

namespace FeatherPod.Shared.Services;

public class TranscriptionService : ITranscriptionService
{
    private readonly AudioClient? _audioClient;
    private readonly ILogger<TranscriptionService> _logger;
    private readonly int _chunkMinutes;
    private readonly int _overlapSeconds;

    private const long MaxWhisperFileSize = 25 * 1024 * 1024; // 25 MB
    private static readonly TimeSpan WhisperTimeout = TimeSpan.FromMinutes(5);

    public bool IsAvailable => _audioClient != null;

    public TranscriptionService(
        string? azureOpenAIEndpoint,
        string? whisperDeployment,
        ILogger<TranscriptionService> logger,
        int chunkMinutes = 12,
        int overlapSeconds = 30)
    {
        _logger = logger;
        _chunkMinutes = chunkMinutes;
        _overlapSeconds = overlapSeconds;

        if (string.IsNullOrEmpty(azureOpenAIEndpoint) || string.IsNullOrEmpty(whisperDeployment))
        {
            _logger.LogInformation("Whisper not configured; transcription disabled");

            return;
        }

        var client = new AzureOpenAIClient(new Uri(azureOpenAIEndpoint), new DefaultAzureCredential());
        _audioClient = client.GetAudioClient(whisperDeployment);

        _logger.LogInformation("Transcription enabled (endpoint: {Endpoint}, deployment: {Deployment})", azureOpenAIEndpoint, whisperDeployment);
    }

    public async Task<string?> TranscribeAsync(string audioPath, Action<ProgressUpdate>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        if (_audioClient == null)
        {
            return null;
        }

        var extension = Path.GetExtension(audioPath);
        var totalDuration = await GetAudioDurationAsync(audioPath, cancellationToken);
        var chunkDuration = TimeSpan.FromMinutes(_chunkMinutes);

        if (totalDuration <= TimeSpan.Zero || totalDuration <= chunkDuration * 1.5)
        {
            var fileSize = new FileInfo(audioPath).Length;
            _logger.LogDebug("Audio {Duration} fits in a single chunk ({Size} bytes), transcribing without chunking", totalDuration, fileSize);
            progressCallback?.Invoke(new()
            {
                Stage = NormalizationStage.Normalizing,
                ProgressPercent = 0,
                Message = "Transcribing"
            });

            var vtt = await TranscribeChunkAsync(audioPath, $"audio{extension}", cancellationToken);

            progressCallback?.Invoke(new()
            {
                Stage = NormalizationStage.Normalizing,
                ProgressPercent = 100,
                Message = "Transcribing"
            });

            return vtt;
        }

        return await TranscribeWithChunkingAsync(audioPath, extension, totalDuration, progressCallback, cancellationToken);
    }

    private async Task<string?> TranscribeWithChunkingAsync(string audioPath, string extension, TimeSpan totalDuration, Action<ProgressUpdate>? progressCallback, CancellationToken cancellationToken)
    {
        var chunkDuration = TimeSpan.FromMinutes(_chunkMinutes);
        var overlap = TimeSpan.FromSeconds(_overlapSeconds);

        var chunks = CalculateChunks(totalDuration, chunkDuration, overlap);
        _logger.LogInformation("Splitting {Duration} audio into {Count} chunks ({ChunkMin}min, {OverlapSec}s overlap)", totalDuration, chunks.Count, _chunkMinutes, _overlapSeconds);

        var tempDir = Path.GetDirectoryName(audioPath)!;
        var chunkFiles = new List<string>();
        var allCues = new List<VttCue>();

        try
        {
            // Split audio into chunks
            for (var i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = chunks[i];
                var chunkPath = Path.Combine(tempDir, $"chunk_{i:D3}{extension}");
                chunkFiles.Add(chunkPath);

                await SplitAudioChunkAsync(audioPath, chunkPath, chunk.Start, chunk.Duration, cancellationToken);
            }

            // Transcribe each chunk sequentially (progress weighted by owned duration)
            var totalOwnedSeconds = totalDuration.TotalSeconds;
            for (var i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var progressPercent = totalOwnedSeconds > 0
                    ? (chunks[i].OwnedStart.TotalSeconds / totalOwnedSeconds) * 100
                    : 0.0;
                progressCallback?.Invoke(new()
                {
                    Stage = NormalizationStage.Normalizing,
                    ProgressPercent = progressPercent,
                    Message = $"Transcribing ({i + 1}/{chunks.Count})"
                });

                var vttText = await TranscribeChunkAsync(chunkFiles[i], $"chunk{extension}", cancellationToken);
                if (vttText == null)
                {
                    _logger.LogWarning("Chunk {Index}/{Total} returned null transcription", i + 1, chunks.Count);

                    continue;
                }

                var cues = ParseVttCues(vttText);
                var chunk = chunks[i];

                // Offset cues to absolute timestamps and filter to owned range
                foreach (var cue in cues)
                {
                    var offsetCue = cue with
                    {
                        Start = cue.Start + chunk.Start,
                        End = cue.End + chunk.Start
                    };

                    if (offsetCue.Start >= chunk.OwnedStart && offsetCue.Start < chunk.OwnedEnd)
                    {
                        allCues.Add(offsetCue);
                    }
                }
            }

            progressCallback?.Invoke(new()
            {
                Stage = NormalizationStage.Normalizing,
                ProgressPercent = 100,
                Message = "Transcribing"
            });

            return SerializeVtt(allCues);
        }
        finally
        {
            foreach (var file in chunkFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    private async Task<string?> TranscribeChunkAsync(string audioPath, string audioFilename, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(WhisperTimeout);

        try
        {
            await using var stream = File.OpenRead(audioPath);
            var result = await _audioClient!.TranscribeAudioAsync(stream, audioFilename, new AudioTranscriptionOptions
            {
                ResponseFormat = AudioTranscriptionFormat.Vtt
            }, timeoutCts.Token);

            return result.Value.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Whisper transcription failed for {Path}", audioPath);

            return null;
        }
    }

    #region FFmpeg audio chunking

    private static async Task<TimeSpan> GetAudioDurationAsync(string audioPath, CancellationToken cancellationToken)
    {
        var mediaInfo = await FFProbe.AnalyseAsync(audioPath, cancellationToken: cancellationToken);

        return mediaInfo.Duration;
    }

    private async Task SplitAudioChunkAsync(string inputPath, string outputPath, TimeSpan start, TimeSpan duration, CancellationToken cancellationToken)
    {
        var ffmpegPath = FFmpegBinaryManager.GetFFmpegPath();
        var startStr = start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
        var durationStr = duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

        using var process = new Process();
        process.StartInfo = new()
        {
            FileName = ffmpegPath,
            Arguments = $"-ss {startStr} -i \"{inputPath}\" -t {durationStr} -c copy -y \"{outputPath}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg chunk split failed with exit code {process.ExitCode}");
        }
    }

    #endregion

    #region Chunk calculation

    internal record ChunkInfo(TimeSpan Start, TimeSpan Duration, TimeSpan OwnedStart, TimeSpan OwnedEnd);

    internal static List<ChunkInfo> CalculateChunks(TimeSpan totalDuration, TimeSpan chunkDuration, TimeSpan overlap)
    {
        var chunks = new List<ChunkInfo>();
        var position = TimeSpan.Zero;

        while (position < totalDuration)
        {
            var isFirst = position == TimeSpan.Zero;
            var start = isFirst ? TimeSpan.Zero : position - overlap;
            var ownedStart = position;
            var remaining = totalDuration - position;
            var ownedEnd = remaining <= chunkDuration * 1.5 ? totalDuration : position + chunkDuration;
            var end = ownedEnd == totalDuration ? totalDuration : ownedEnd + overlap;
            var duration = end - start;

            chunks.Add(new ChunkInfo(start, duration, ownedStart, ownedEnd));
            position = ownedEnd;
        }

        return chunks;
    }

    #endregion

    #region VTT parsing and stitching

    internal record VttCue(TimeSpan Start, TimeSpan End, string Text);

    internal static List<VttCue> ParseVttCues(string vttText)
    {
        var cues = new List<VttCue>();
        var lines = vttText.Split('\n', StringSplitOptions.None);

        var i = 0;
        // Skip WEBVTT header and any blank lines
        while (i < lines.Length && !lines[i].Contains("-->"))
        {
            i++;
        }

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line.Contains("-->"))
            {
                var parts = line.Split("-->");
                if (parts.Length == 2 &&
                    TryParseVttTimestamp(parts[0].Trim(), out var start) &&
                    TryParseVttTimestamp(parts[1].Trim(), out var end))
                {
                    i++;
                    var textBuilder = new StringBuilder();
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !lines[i].Contains("-->"))
                    {
                        if (textBuilder.Length > 0)
                        {
                            textBuilder.Append('\n');
                        }
                        textBuilder.Append(lines[i].Trim());
                        i++;
                    }

                    if (textBuilder.Length > 0)
                    {
                        cues.Add(new VttCue(start, end, textBuilder.ToString()));
                    }
                }
                else
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return cues;
    }

    internal static bool TryParseVttTimestamp(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        // VTT timestamps: HH:MM:SS.mmm or MM:SS.mmm
        var parts = text.Split(':');

        try
        {
            if (parts.Length == 3)
            {
                var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
                var seconds = double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
                result = new TimeSpan(0, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);

                return true;
            }

            if (parts.Length == 2)
            {
                var minutes = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var seconds = double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                result = new TimeSpan(0, 0, minutes, 0) + TimeSpan.FromSeconds(seconds);

                return true;
            }
        }
        catch (FormatException)
        {
            // Fall through to return false
        }

        return false;
    }

    internal static string FormatVttTimestamp(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    internal static string SerializeVtt(List<VttCue> cues)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var cue in cues)
        {
            sb.AppendLine($"{FormatVttTimestamp(cue.Start)} --> {FormatVttTimestamp(cue.End)}");
            sb.AppendLine(cue.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion
}
