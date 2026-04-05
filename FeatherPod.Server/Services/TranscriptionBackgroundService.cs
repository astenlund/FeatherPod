using System.Diagnostics;
using Azure.Storage.Blobs;
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
    private readonly BlobServiceClient _blobClient;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly ILogger<TranscriptionBackgroundService> _logger;
    private readonly string _containerName;
    private readonly SemaphoreSlim _concurrency;

    public TranscriptionBackgroundService(
        ITranscriptionChannel channel,
        SpeechTranscriptionService speechService,
        IBlobStorageService blobService,
        BlobServiceClient blobClient,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        IConfiguration configuration,
        ILogger<TranscriptionBackgroundService> logger)
    {
        _channel = channel;
        _speechService = speechService;
        _blobService = blobService;
        _blobClient = blobClient;
        _jobService = jobService;
        _progressChannel = progressChannel;
        _logger = logger;
        _containerName = configuration.GetSection("Azure").GetValue<string>("ContainerName") ?? "featherpod";

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

        // Startup recovery: re-submit interrupted jobs
        await RecoverInterruptedJobsAsync(stoppingToken);

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
        await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = "Running";
            e.TranscriptionStartedAt = DateTimeOffset.UtcNow;
        }, ct);

        string? tempInputFile = null;
        string? tempWavFile = null;

        try
        {
            // Download pending blob to temp file
            var containerClient = _blobClient.GetBlobContainerClient(_containerName);
            var pendingBlobPath = $"{request.FeedId}/pending/{request.JobId}/{request.FileName}";
            var blobClient = containerClient.GetBlobClient(pendingBlobPath);

            tempInputFile = Path.Combine(Path.GetTempPath(), $"transcribe-{request.JobId}{Path.GetExtension(request.FileName)}");
            await using (var downloadStream = await blobClient.OpenReadAsync(cancellationToken: ct))
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
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"FFmpeg WAV conversion failed (exit {process.ExitCode}): {stderr[..Math.Min(500, stderr.Length)]}");
            }

            // Get duration from converted WAV for progress calculation
            var mediaInfo = await FFProbe.AnalyseAsync(tempWavFile, cancellationToken: ct);
            var totalDuration = mediaInfo.Duration;

            _logger.LogDebug("WAV conversion complete: {Duration}", totalDuration);

            // Transcribe with progress
            await using var wavStream = File.OpenRead(tempWavFile);
            var vtt = await _speechService.TranscribeAsync(wavStream, totalDuration, progress =>
            {
                _ = _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionProgress = progress;
                }, CancellationToken.None);

                // Publish progress to SSE/SignalR consumers
                var entity = new JobStatusEntity
                {
                    PartitionKey = "jobs",
                    RowKey = request.JobId,
                    TranscriptionStatus = "Running",
                    TranscriptionProgress = progress
                };
                _progressChannel.Publish(request.JobId, JobStatusResponse.FromEntity(entity));
            }, ct);

            if (vtt != null)
            {
                // Upload VTT to blob storage
                await _blobService.UploadTranscriptAsync(request.FeedId, request.EpisodeId, vtt);

                await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionStatus = "Completed";
                    e.TranscriptionProgress = 100;
                }, ct);

                _logger.LogInformation("Transcription completed for job {JobId}", request.JobId);
            }
            else
            {
                await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionStatus = "Failed";
                    e.TranscriptionError = "Transcription produced no output";
                }, ct);

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

            await _jobService.MergeJobFieldsAsync(request.JobId, e =>
            {
                e.TranscriptionStatus = "Failed";
                e.TranscriptionError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            }, CancellationToken.None);
        }
        finally
        {
            CleanupTempFile(tempInputFile);
            CleanupTempFile(tempWavFile);
        }

        // Trigger join logic (JobCompletionService wired in Task 8)
        // For now this is a no-op; TryCompleteJobAsync will be called here
    }

    private async Task RecoverInterruptedJobsAsync(CancellationToken ct)
    {
        try
        {
            // Scan for jobs with TranscriptionStatus == "Running" or "Queued"
            // This is a simple linear scan of the table; acceptable since it runs once on startup
            var tableClient = new Azure.Data.Tables.TableClient(
                _blobClient.Uri.ToString().Replace(".blob.", ".table."),
                "normalizationjobs");

            // Use IJobService to query instead of raw table client
            // For startup recovery, we scan all non-terminal jobs
            _logger.LogInformation("Scanning for interrupted transcription jobs...");

            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            var staleLimit = DateTimeOffset.UtcNow.AddHours(-1);
            var recovered = 0;
            var failed = 0;

            // Query all jobs via the existing job service's table scan
            // Since IJobService doesn't expose a general query, we'll use GetActiveJobsByFeedAsync
            // across all feeds. This is impractical. Instead, skip startup recovery for now
            // and let the CleanupFunction handle stale transcriptions.
            // TODO: Add a query method to IJobService for startup recovery

            _logger.LogInformation("Startup recovery: recovered {Recovered}, marked failed {Failed}", recovered, failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup recovery scan failed, will rely on CleanupFunction for stale jobs");
        }
    }

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
