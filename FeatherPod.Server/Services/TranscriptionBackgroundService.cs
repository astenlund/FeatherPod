using System.Diagnostics;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Server.Services;

/// <summary>
/// Background service that processes transcription requests from the in-memory channel.
/// Routes each request to either the Fast Transcription endpoint (synchronous, ~2-6x realtime)
/// or the batch endpoint (fallback for audio &gt; <c>AzureSpeech:FastMaxDurationMinutes</c>,
/// or when Fast returns <see cref="FastTranscriptionUnavailableException"/>).
/// M4A/M4B/MP4 are converted to 16 kHz mono WAV in both paths because the Speech APIs
/// don't accept the MP4 container despite "AAC" appearing in their format list.
/// Bounded concurrency via SemaphoreSlim.
/// </summary>
public class TranscriptionBackgroundService : BackgroundService
{
    /// <summary>
    /// Extensions that need conversion to WAV for either Speech endpoint.
    /// The APIs don't support the MP4/M4A container despite listing "AAC" as supported.
    /// </summary>
    private static readonly HashSet<string> ConvertExtensions = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".m4b", ".mp4" };

    private readonly ITranscriptionChannel _channel;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IBlobStorageService _blobService;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly JobCompletionService _completionService;
    private readonly IAudioDurationProbe _durationProbe;
    private readonly ILogger<TranscriptionBackgroundService> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly bool _useFastTranscription;
    private readonly TimeSpan _fastMaxDuration;

    public TranscriptionBackgroundService(
        ITranscriptionChannel channel,
        ISpeechTranscriptionService speechService,
        IBlobStorageService blobService,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        JobCompletionService completionService,
        IAudioDurationProbe durationProbe,
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
        _durationProbe = durationProbe;
        _logger = logger;

        lifetime.ApplicationStopping.Register(() => _channel.Complete());

        var maxConcurrent = configuration.GetValue("AzureSpeech:MaxConcurrent", 5);
        _concurrency = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _useFastTranscription = configuration.GetValue("AzureSpeech:UseFastTranscription", true);
        _fastMaxDuration = TimeSpan.FromMinutes(configuration.GetValue("AzureSpeech:FastMaxDurationMinutes", 110));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_speechService.IsAvailable)
        {
            _logger.LogInformation("Speech transcription disabled, TranscriptionBackgroundService idle");

            return;
        }

        _logger.LogInformation(
            "TranscriptionBackgroundService started (UseFast={UseFast}, FastMaxMinutes={FastMaxMinutes})",
            _useFastTranscription,
            _fastMaxDuration.TotalMinutes);

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

        var running = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Running;
            e.TranscriptionStartedAt = DateTimeOffset.UtcNow;
        }, ct);
        PublishProgress(request.JobId, running, "Running");

        string? tempInputFile = null;
        string? tempWavFile = null;
        var needsConversion = NeedsConversion(request.FileName);
        var transcriptionResolved = false;

        try
        {
            if (_useFastTranscription)
            {
                tempInputFile = await DownloadPendingBlobToTempAsync(request, ct);

                if (needsConversion)
                {
                    tempWavFile = await ConvertToWavAsync(tempInputFile, request.JobId, ct);
                }

                var probePath = tempWavFile ?? tempInputFile;
                var duration = await _durationProbe.GetDurationAsync(probePath, ct);

                if (duration <= _fastMaxDuration)
                {
                    try
                    {
                        await TranscribeViaFastAsync(request, probePath, ct);
                        transcriptionResolved = true;
                    }
                    catch (FastTranscriptionUnavailableException ex)
                    {
                        _logger.LogInformation(
                            "Fast transcription unavailable for job {JobId}, falling back to batch: {Reason}",
                            request.JobId,
                            ex.Message);
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "Audio duration {DurationMinutes:F1}m exceeds Fast cap {CapMinutes}m for job {JobId}, using batch",
                        duration.TotalMinutes,
                        _fastMaxDuration.TotalMinutes,
                        request.JobId);
                }
            }

            if (!transcriptionResolved)
            {
                // Reuse already-downloaded/converted artifacts when falling back from Fast.
                // For a batch-only run (Fast disabled or duration > cap) on M4A/M4B/MP4, do
                // the download + WAV conversion now so the batch upload has a local file.
                if (needsConversion)
                {
                    tempInputFile ??= await DownloadPendingBlobToTempAsync(request, ct);
                    tempWavFile ??= await ConvertToWavAsync(tempInputFile, request.JobId, ct);
                }

                await TranscribeViaBatchAsync(request, tempWavFile, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription failed for job {JobId}", request.JobId);
            await MarkFailedAsync(request, ex.Message, "Failed (exception)", CancellationToken.None);
        }
        finally
        {
            FileHelper.TryDeleteFile(tempInputFile, _logger);
            FileHelper.TryDeleteFile(tempWavFile, _logger);
        }

        await _completionService.TryCompleteJobAsync(request.JobId, CancellationToken.None);
    }

    private async Task TranscribeViaFastAsync(TranscriptionRequest request, string filePath, CancellationToken ct)
    {
        // M4A/M4B/MP4 always go through ConvertToWavAsync, so filePath ends in .wav for those.
        var contentType = AudioHelper.GetMimeType(filePath);
        var blobFileName = Path.GetFileName(filePath);

        await using var fileStream = File.OpenRead(filePath);
        var vtt = await _speechService.TranscribeFastAsync(fileStream, contentType, blobFileName, ct);

        if (vtt is null)
        {
            await MarkFailedNoOutputAsync(request, "Fast", ct);

            return;
        }

        await MarkCompletedAsync(request, vtt, "Fast", ct);
    }

    private async Task TranscribeViaBatchAsync(TranscriptionRequest request, string? tempWavFile, CancellationToken ct)
    {
        // Batch needs an Azure-reachable URL. A non-null tempWavFile means the caller already
        // converted M4A/M4B/MP4 to WAV; upload it as a sibling blob. Otherwise (MP3/WAV/OGG/FLAC)
        // use the original pending blob via SAS.
        var sasFileName = request.FileName;

        if (tempWavFile is not null)
        {
            sasFileName = $"_transcription-{request.JobId}.wav";
            await _blobService.UploadPendingAudioAsync(request.FeedId, request.JobId, sasFileName, tempWavFile);
        }

        var sasUrl = await _blobService.GeneratePendingBlobSasUrlAsync(request.FeedId, request.JobId, sasFileName);
        var transcriptionUrl = await _speechService.SubmitAsync(sasUrl, ct);

        try
        {
            var (status, filesListUrl, errorMessage) = await _speechService.PollUntilCompleteAsync(transcriptionUrl, ct);

            if (status is BatchTranscriptionApi.FailedStatus || filesListUrl is null)
            {
                var error = errorMessage ?? "Batch transcription failed on Azure side";
                _logger.LogWarning("Batch transcription failed for job {JobId}: {Error}", request.JobId, error);
                await MarkFailedAsync(request, error, "Failed (Azure)", ct);

                return;
            }

            var vtt = await _speechService.GetResultAsVttAsync(filesListUrl, ct);

            if (vtt != null)
            {
                await MarkCompletedAsync(request, vtt, "Batch", ct);
            }
            else
            {
                await MarkFailedNoOutputAsync(request, "Batch", ct);
            }
        }
        finally
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
    }

    private async Task MarkCompletedAsync(TranscriptionRequest request, string vtt, string mode, CancellationToken ct)
    {
        await _blobService.UploadTranscriptAsync(request.FeedId, request.EpisodeId, vtt);

        var completed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Completed;
        }, ct);
        PublishProgress(request.JobId, completed, "Completed");
        _logger.LogInformation("{Mode} transcription completed for job {JobId}", mode, request.JobId);
    }

    private async Task MarkFailedNoOutputAsync(TranscriptionRequest request, string mode, CancellationToken ct)
    {
        await MarkFailedAsync(request, "Transcription produced no output", "Failed (no output)", ct);
        _logger.LogWarning("{Mode} transcription produced no output for job {JobId}", mode, request.JobId);
    }

    private async Task MarkFailedAsync(TranscriptionRequest request, string error, string stage, CancellationToken ct)
    {
        var failed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Failed;
            e.TranscriptionError = error.Truncate(500);
        }, ct);
        PublishProgress(request.JobId, failed, stage);
    }

    /// <summary>
    /// Whether the file extension requires conversion to WAV for the Speech APIs.
    /// </summary>
    private static bool NeedsConversion(string fileName)
    {
        return ConvertExtensions.Contains(Path.GetExtension(fileName));
    }

    private static string GetOrCreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FeatherPod");
        Directory.CreateDirectory(tempDir);

        return tempDir;
    }

    private async Task<string> DownloadPendingBlobToTempAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var tempInputFile = Path.Combine(GetOrCreateTempDir(), $"transcribe-{request.JobId}{Path.GetExtension(request.FileName)}");

        await using (var downloadStream = await _blobService.DownloadPendingBlobAsync(request.FeedId, request.JobId, request.FileName))
        await using (var fileStream = File.Create(tempInputFile))
        {
            await downloadStream.CopyToAsync(fileStream, ct);
        }

        return tempInputFile;
    }

    /// <summary>
    /// Convert <paramref name="tempInputFile"/> to 16 kHz mono WAV via FFmpeg. Returns the
    /// temp WAV path. Pure local I/O -- no blob upload (callers do that separately for batch).
    /// </summary>
    private async Task<string> ConvertToWavAsync(string tempInputFile, string jobId, CancellationToken ct)
    {
        var tempWavFile = Path.Combine(GetOrCreateTempDir(), $"transcribe-{jobId}.wav");

        var ffmpegPath = FFmpegBinaryManager.GetFFmpegPath();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList = { "-i", tempInputFile, "-ar", "16000", "-ac", "1", "-f", "wav", tempWavFile, "-y" },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
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
            jobId, new FileInfo(tempInputFile).Length, new FileInfo(tempWavFile).Length);

        return tempWavFile;
    }

    private void PublishProgress(string jobId, JobStatusEntity? entity, string stage)
    {
        if (entity != null)
        {
            _progressChannel.Publish(jobId, JobStatusResponse.FromEntity(entity));

            return;
        }

        _logger.LogWarning(
            "Failed to publish {Stage} progress for job {JobId}: merge returned null (see prior log for cause)",
            stage,
            jobId);
    }
}
