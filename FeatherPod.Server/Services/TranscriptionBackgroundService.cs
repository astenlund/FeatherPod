using System.Diagnostics;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FFMpegCore;

namespace FeatherPod.Server.Services;

/// <summary>
/// Background service that processes transcription requests from the in-memory channel.
/// Downloads pending blob, converts to 16kHz WAV, transcribes via Speech SDK, uploads VTT.
/// Bounded concurrency via SemaphoreSlim.
/// </summary>
public class TranscriptionBackgroundService : BackgroundService
{
    private readonly ITranscriptionChannel _channel;
    private readonly SpeechTranscriptionService _speechService;
    private readonly IBlobStorageService _blobService;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly JobCompletionService _completionService;
    private readonly ILogger<TranscriptionBackgroundService> _logger;
    private readonly SemaphoreSlim _concurrency;

    public TranscriptionBackgroundService(
        ITranscriptionChannel channel,
        SpeechTranscriptionService speechService,
        IBlobStorageService blobService,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        JobCompletionService completionService,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        ILogger<TranscriptionBackgroundService> logger)
    {
        _channel = channel;
        _speechService = speechService;
        _blobService = blobService;
        _jobService = jobService;
        _progressChannel = progressChannel;
        _completionService = completionService;
        _logger = logger;

        // Stop accepting new requests on shutdown; in-flight sessions get stoppingToken cancellation
        lifetime.ApplicationStopping.Register(() =>
        {
            if (_channel is TranscriptionChannel tc)
            {
                tc.Complete();
            }
        });

        var maxConcurrent = configuration.GetValue("AzureSpeech:MaxConcurrent", 3);
        _concurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_speechService.IsAvailable)
        {
            _logger.LogInformation("Speech transcription disabled, TranscriptionBackgroundService idle");

            return;
        }

        _logger.LogInformation("TranscriptionBackgroundService started, processing requests");

        await foreach (var request in _channel.ReadAllAsync(stoppingToken))
        {
            await _concurrency.WaitAsync(stoppingToken);
            _ = ProcessAndReleaseAsync(request, stoppingToken);
        }
    }

    private async Task ProcessAndReleaseAsync(TranscriptionRequest request, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessTranscriptionAsync(request, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down, leave job as Running for startup recovery
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing transcription for job {JobId}", request.JobId);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task ProcessTranscriptionAsync(TranscriptionRequest request, CancellationToken ct)
    {
        _logger.LogInformation("Starting transcription for job {JobId}", request.JobId);

        // Mark transcription as running
        var running = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Running;
            e.TranscriptionStartedAt = DateTimeOffset.UtcNow;
        }, ct);
        PublishProgress(request.JobId, running);

        string? tempInputFile = null;
        string? tempWavFile = null;

        try
        {
            // Download pending blob to temp file
            tempInputFile = Path.Combine(Path.GetTempPath(), $"transcribe-{request.JobId}{Path.GetExtension(request.FileName)}");
            await using (var downloadStream = await _blobService.DownloadPendingBlobAsync(request.FeedId, request.JobId, request.FileName))
            await using (var fileStream = File.Create(tempInputFile))
            {
                await downloadStream.CopyToAsync(fileStream, ct);
            }

            _logger.LogDebug("Downloaded pending blob for transcription: {Size} bytes", new FileInfo(tempInputFile).Length);

            // Convert to 16kHz mono WAV PCM via FFmpeg
            tempWavFile = Path.Combine(Path.GetTempPath(), $"transcribe-{request.JobId}.wav");
            var ffmpegPath = FFmpegBinaryManager.GetFFmpegPath();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{tempInputFile}\" -ar 16000 -ac 1 -f wav \"{tempWavFile}\" -y",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            // Read stderr before WaitForExit to avoid pipe buffer deadlock
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg WAV conversion failed (exit {process.ExitCode}): {stderr[..Math.Min(500, stderr.Length)]}");
            }

            // Get duration from converted WAV for progress calculation
            var ffprobeBinaryDir = Path.GetDirectoryName(FFmpegBinaryManager.GetFFprobePath());
            var ffOptions = new FFOptions { BinaryFolder = ffprobeBinaryDir ?? string.Empty };
            var mediaInfo = await FFProbe.AnalyseAsync(tempWavFile, ffOptions, ct);
            var totalDuration = mediaInfo.Duration;

            _logger.LogDebug("WAV conversion complete: {Duration}", totalDuration);

            // Transcribe with progress (throttled to avoid hammering Table Storage)
            await using var wavStream = File.OpenRead(tempWavFile);
            var lastProgressUpdate = DateTime.MinValue;
            var progressThrottle = TimeSpan.FromMilliseconds(500);
            var vtt = await _speechService.TranscribeAsync(wavStream, totalDuration, progress =>
            {
                var now = DateTime.UtcNow;
                if (progress < 100 && now - lastProgressUpdate < progressThrottle)
                {
                    return;
                }

                lastProgressUpdate = now;

                _ = Task.Run(async () =>
                {
                    var merged = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                    {
                        e.TranscriptionProgress = progress;
                    }, CancellationToken.None);

                    if (merged != null)
                    {
                        _progressChannel.Publish(request.JobId, JobStatusResponse.FromEntity(merged));
                    }
                });
            }, ct);

            if (vtt != null)
            {
                // Upload VTT to blob storage
                await _blobService.UploadTranscriptAsync(request.FeedId, request.EpisodeId, vtt);

                var completed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionStatus = TranscriptionStatuses.Completed;
                    e.TranscriptionProgress = 100;
                }, ct);
                PublishProgress(request.JobId, completed);

                _logger.LogInformation("Transcription completed for job {JobId}", request.JobId);
            }
            else
            {
                var noOutput = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionStatus = TranscriptionStatuses.Failed;
                    e.TranscriptionError = "Transcription produced no output";
                }, ct);
                PublishProgress(request.JobId, noOutput);

                _logger.LogWarning("Transcription produced no output for job {JobId}", request.JobId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription failed for job {JobId}", request.JobId);

            var failed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
            {
                e.TranscriptionStatus = TranscriptionStatuses.Failed;
                e.TranscriptionError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            }, CancellationToken.None);
            PublishProgress(request.JobId, failed);
        }
        finally
        {
            CleanupTempFile(tempInputFile);
            CleanupTempFile(tempWavFile);
        }

        // Trigger join logic — if normalization is also done, this creates the episode
        await _completionService.TryCompleteJobAsync(request.JobId, CancellationToken.None);
    }

    private void PublishProgress(string jobId, JobStatusEntity? entity)
    {
        if (entity != null)
        {
            _progressChannel.Publish(jobId, JobStatusResponse.FromEntity(entity));
        }
    }

    // TODO: Add startup recovery for interrupted transcription jobs
    // Requires a general query method on IJobService to scan for TranscriptionStatus == "Running"/"Queued".
    // Until then, the CleanupFunction handles stale transcriptions (>24h timeout).

    private void CleanupTempFile(string? path)
    {
        if (path == null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp file {Path}", path);
        }
    }
}
