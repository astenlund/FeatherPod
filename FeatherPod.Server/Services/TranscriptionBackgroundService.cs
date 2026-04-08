using System.Diagnostics;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

/// <summary>
/// Background service that processes transcription requests from the in-memory channel.
/// Submits audio to the batch transcription REST API via SAS URL, polls for completion,
/// downloads the result, and uploads VTT.
/// M4A/M4B files are converted to 16kHz mono WAV before submission (the batch API doesn't
/// support the MP4 container despite listing "AAC" as supported). Other formats (MP3, WAV,
/// OGG, FLAC) go directly via SAS URL.
/// Bounded concurrency via SemaphoreSlim.
/// </summary>
public class TranscriptionBackgroundService : BackgroundService
{
    /// <summary>
    /// Extensions that need conversion to WAV for the batch Speech API.
    /// The API doesn't support the MP4/M4A container despite listing "AAC" as supported.
    /// </summary>
    private static readonly HashSet<string> ConvertExtensions = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".m4b", ".mp4" };

    private readonly ITranscriptionChannel _channel;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IBlobStorageService _blobService;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly JobCompletionService _completionService;
    private readonly ILogger<TranscriptionBackgroundService> _logger;
    private readonly SemaphoreSlim _concurrency;

    public TranscriptionBackgroundService(
        ITranscriptionChannel channel,
        ISpeechTranscriptionService speechService,
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

        lifetime.ApplicationStopping.Register(() => _channel.Complete());

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
        _logger.LogInformation("Starting batch transcription for job {JobId}", request.JobId);

        var running = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Running;
            e.TranscriptionStartedAt = DateTimeOffset.UtcNow;
        }, ct);
        PublishProgress(request.JobId, running);

        string? transcriptionUrl = null;
        string? tempInputFile = null;
        string? tempWavFile = null;

        try
        {
            var sasFileName = request.FileName;

            if (NeedsConversion(request.FileName))
            {
                (sasFileName, tempInputFile, tempWavFile) = await ConvertToWavAsync(request, ct);
            }

            var sasUrl = await _blobService.GeneratePendingBlobSasUrlAsync(request.FeedId, request.JobId, sasFileName);
            transcriptionUrl = await _speechService.SubmitAsync(sasUrl, ct);

            var (status, filesListUrl, errorMessage) = await _speechService.PollUntilCompleteAsync(transcriptionUrl, ct);

            if (status is "Failed" || filesListUrl is null)
            {
                var error = errorMessage ?? "Batch transcription failed on Azure side";
                var failed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                {
                    e.TranscriptionStatus = TranscriptionStatuses.Failed;
                    e.TranscriptionError = error.Truncate(500);
                }, ct);
                PublishProgress(request.JobId, failed);
                _logger.LogWarning("Batch transcription failed for job {JobId}: {Error}", request.JobId, error);
            }
            else
            {
                var vtt = await _speechService.GetResultAsVttAsync(filesListUrl, ct);

                if (vtt != null)
                {
                    await _blobService.UploadTranscriptAsync(request.FeedId, request.EpisodeId, vtt);

                    var completed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                    {
                        e.TranscriptionStatus = TranscriptionStatuses.Completed;
                    }, ct);
                    PublishProgress(request.JobId, completed);
                    _logger.LogInformation("Batch transcription completed for job {JobId}", request.JobId);
                }
                else
                {
                    var noOutput = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
                    {
                        e.TranscriptionStatus = TranscriptionStatuses.Failed;
                        e.TranscriptionError = "Transcription produced no output";
                    }, ct);
                    PublishProgress(request.JobId, noOutput);
                    _logger.LogWarning("Batch transcription produced no output for job {JobId}", request.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch transcription failed for job {JobId}", request.JobId);

            var failed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
            {
                e.TranscriptionStatus = TranscriptionStatuses.Failed;
                e.TranscriptionError = ex.Message.Truncate(500);
            }, CancellationToken.None);
            PublishProgress(request.JobId, failed);
        }
        finally
        {
            if (transcriptionUrl != null)
            {
                try
                {
                    await _speechService.DeleteAsync(transcriptionUrl, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete batch transcription {Url} for job {JobId}", transcriptionUrl, request.JobId);
                }
            }

            FileHelper.TryDeleteFile(tempInputFile, _logger);
            FileHelper.TryDeleteFile(tempWavFile, _logger);
        }

        await _completionService.TryCompleteJobAsync(request.JobId, CancellationToken.None);
    }

    /// <summary>
    /// Whether the file extension requires conversion to WAV for the batch Speech API.
    /// </summary>
    private static bool NeedsConversion(string fileName)
    {
        return ConvertExtensions.Contains(Path.GetExtension(fileName));
    }

    /// <summary>
    /// Download the pending blob, convert to 16kHz mono WAV via FFmpeg,
    /// and upload the result as a sibling blob. Returns the new blob filename for SAS generation.
    /// </summary>
    private async Task<(string BlobFileName, string TempInputFile, string TempWavFile)> ConvertToWavAsync(
        TranscriptionRequest request, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);
        var tempInputFile = Path.Combine(tempDir, $"transcribe-{request.JobId}{Path.GetExtension(request.FileName)}");
        var tempWavFile = Path.Combine(tempDir, $"transcribe-{request.JobId}.wav");

        await using (var downloadStream = await _blobService.DownloadPendingBlobAsync(request.FeedId, request.JobId, request.FileName))
        await using (var fileStream = File.Create(tempInputFile))
        {
            await downloadStream.CopyToAsync(fileStream, ct);
        }

        var ffmpegPath = FFmpegBinaryManager.GetFFmpegPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList = { "-i", tempInputFile, "-ar", "16000", "-ac", "1", "-f", "wav", tempWavFile, "-y" },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
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

        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg WAV conversion failed (exit {process.ExitCode}): {stderr.Truncate(500)}");
        }

        _logger.LogInformation("Converted to WAV for transcription job {JobId}: {InputSize} -> {OutputSize} bytes",
            request.JobId, new FileInfo(tempInputFile).Length, new FileInfo(tempWavFile).Length);

        var wavBlobName = $"_transcription-{request.JobId}.wav";
        await _blobService.UploadPendingAudioAsync(request.FeedId, request.JobId, wavBlobName, tempWavFile);

        return (wavBlobName, tempInputFile, tempWavFile);
    }

    private void PublishProgress(string jobId, JobStatusEntity? entity)
    {
        if (entity != null)
        {
            _progressChannel.Publish(jobId, JobStatusResponse.FromEntity(entity));
        }
    }
}
