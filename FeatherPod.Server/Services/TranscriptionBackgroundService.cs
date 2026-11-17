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
/// don't accept the MP4 container despite "AAC" appearing in their format list. With Fast enabled,
/// MP3/WAV/OGG/FLAC are probed for duration through a SAS URL first, so audio over the cap goes to
/// batch without a download; if that probe fails they are downloaded and probed locally (see
/// <see cref="TryProbeSourceAsync"/>). Other formats are downloaded before probing. The source is the job's pending upload, or for a
/// transcript retry (<see cref="TranscriptionRequest.FromEpisodeAudio"/>) the published episode audio. Every run stages its
/// transcript for its own attempt (BlobPaths.PendingTranscript), and only the job's join publishes it; every status write and temp file
/// is scoped to the run's attempt, so a run of an earlier attempt records nothing on a retried job.
/// Bounded concurrency via SemaphoreSlim. On startup, jobs a previous process left Queued or Running
/// are resubmitted (see <see cref="RecoverInterruptedTranscriptionsAsync"/>).
/// </summary>
public class TranscriptionBackgroundService : BackgroundService
{
    /// <summary>
    /// Extensions that need conversion to WAV for either Speech endpoint.
    /// The APIs don't support the MP4/M4A container despite listing "AAC" as supported.
    /// </summary>
    private static readonly HashSet<string> ConvertExtensions = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".m4b", ".mp4" };

    /// <summary>
    /// Extensions whose duration is probed through a SAS URL before any download. They match the formats
    /// <see cref="IAudioDurationProbe"/>'s URL method reads; every other name keeps the download-then-probe order.
    /// </summary>
    private static readonly HashSet<string> UrlProbeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".flac" };

    /// <summary>
    /// Transcription statuses a restart can leave behind with no request in the in-memory channel.
    /// </summary>
    private static readonly string[] InterruptedTranscriptionStatuses = [TranscriptionStatuses.Queued, TranscriptionStatuses.Running];

    private readonly ITranscriptionChannel _channel;
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IBlobStorageService _blobService;
    private readonly IJobService _jobService;
    private readonly IJobProgressChannel _progressChannel;
    private readonly JobCompletionService _completionService;
    private readonly IAudioDurationProbe _durationProbe;
    private readonly IWavConverter _wavConverter;
    private readonly ILogger<TranscriptionBackgroundService> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly bool _useFastTranscription;
    private readonly TimeSpan _fastMaxDuration;

    /// <summary>
    /// When this instance was created. Hosted services are constructed before the server accepts requests, so any job
    /// queued earlier was submitted by a previous process; later ones were submitted to this instance's channel.
    /// </summary>
    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

    public TranscriptionBackgroundService(
        ITranscriptionChannel channel,
        ISpeechTranscriptionService speechService,
        IBlobStorageService blobService,
        IJobService jobService,
        IJobProgressChannel progressChannel,
        JobCompletionService completionService,
        IAudioDurationProbe durationProbe,
        IWavConverter wavConverter,
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
        _wavConverter = wavConverter;
        _logger = logger;

        lifetime.ApplicationStopping.Register(() => _channel.Complete());

        var maxConcurrent = configuration.GetValue("AzureSpeech:MaxConcurrent", 3);
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

        await RecoverInterruptedTranscriptionsAsync(stoppingToken);

        await foreach (var request in _channel.ReadAllAsync(stoppingToken))
        {
            await _concurrency.WaitAsync(stoppingToken);
            _ = ProcessAndReleaseAsync(request, stoppingToken);
        }
    }

    /// <summary>
    /// Resubmit transcriptions a previous process left Queued or Running: its in-memory channel and in-flight work were
    /// lost on restart, and the join that creates the episode waits for a terminal transcription status. Only jobs queued
    /// before this instance was created are resubmitted, so a job this instance accepted is never submitted twice.
    /// A failed scan is logged and leaves normal channel processing running.
    /// </summary>
    internal async Task RecoverInterruptedTranscriptionsAsync(CancellationToken ct)
    {
        List<JobStatusEntity> jobs;
        try
        {
            jobs = await _jobService.GetActiveJobsByTranscriptionStatusAsync(InterruptedTranscriptionStatuses, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription startup recovery failed to scan for interrupted jobs");

            return;
        }

        var resubmitted = 0;
        foreach (var job in jobs.Where(j => j.QueuedAt is null || j.QueuedAt < _createdAt))
        {
            if (job.FeedId is null || job.FileName is null || job.EpisodeId is null)
            {
                _logger.LogWarning("Cannot recover transcription for job {JobId}: feed, file name or episode id missing", job.RowKey);

                continue;
            }

            await _channel.SubmitAsync(new TranscriptionRequest
            {
                JobId = job.RowKey,
                FeedId = job.FeedId,
                FileName = job.FileName,
                EpisodeId = job.EpisodeId,
                Attempt = job.GetAttempt(),
                FromEpisodeAudio = JobKinds.IsTranscriptRetry(job.Kind),
            }, ct);
            resubmitted++;
        }

        if (resubmitted > 0)
        {
            _logger.LogInformation("Transcription startup recovery resubmitted {Count} interrupted job(s)", resubmitted);
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
        }, ct, RunAttempt(request), TrackOpen);
        PublishProgress(request.JobId, running, "Running");

        string? tempInputFile = null;
        string? tempWavFile = null;
        var needsConversion = NeedsConversion(request.FileName);
        var transcriptionResolved = false;

        try
        {
            if (_useFastTranscription)
            {
                TimeSpan duration;

                if (needsConversion)
                {
                    tempInputFile = await DownloadSourceToTempAsync(request, ct);
                    tempWavFile = await ConvertToWavAsync(tempInputFile, request, ct);
                    duration = await _durationProbe.GetDurationAsync(tempWavFile, ct);
                }
                else if (UrlProbeExtensions.Contains(Path.GetExtension(request.FileName)) && await TryProbeSourceAsync(request, ct) is { } remoteDuration)
                {
                    duration = remoteDuration;
                }
                else
                {
                    tempInputFile = await DownloadSourceToTempAsync(request, ct);
                    duration = await _durationProbe.GetDurationAsync(tempInputFile, ct);
                }

                if (duration <= _fastMaxDuration)
                {
                    tempInputFile ??= await DownloadSourceToTempAsync(request, ct);

                    try
                    {
                        await TranscribeViaFastAsync(request, tempWavFile ?? tempInputFile, ct);
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
                    tempInputFile ??= await DownloadSourceToTempAsync(request, ct);
                    tempWavFile ??= await ConvertToWavAsync(tempInputFile, request, ct);
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
        // converted M4A/M4B/MP4 to WAV; upload it as a sibling blob under the job's pending blobs.
        // Otherwise (MP3/WAV/OGG/FLAC) use the source audio itself via SAS.
        string sasUrl;
        if (tempWavFile is not null)
        {
            var wavFileName = $"_transcription-{request.JobId}-a{RunAttempt(request)}.wav";
            await _blobService.UploadPendingAudioAsync(request.FeedId, request.JobId, wavFileName, tempWavFile);
            sasUrl = await _blobService.GeneratePendingBlobSasUrlAsync(request.FeedId, request.JobId, wavFileName);
        }
        else
        {
            sasUrl = await GenerateSourceSasUrlAsync(request);
        }

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

    /// <summary>
    /// The attempt a run writes for: a run carries the attempt it was started for, and a request without one belongs to the first.
    /// </summary>
    private static int RunAttempt(TranscriptionRequest request) => request.Attempt ?? 1;

    /// <summary>
    /// Whether a run may still write its job's transcription track: only while the track is open (Queued, Running, or unset on a row
    /// written before tracks were recorded). Once something else ended the track (the recurring recovery failing a stalled retry, a
    /// cancel), a late run writes nothing, so an ended outcome stays final. Checked on the read each write is conditional on.
    /// </summary>
    private static bool TrackOpen(JobStatusEntity job) => job.TranscriptionStatus is null or TranscriptionStatuses.Queued or TranscriptionStatuses.Running;

    private async Task MarkCompletedAsync(TranscriptionRequest request, string vtt, string mode, CancellationToken ct)
    {
        // Every run stages its result for its own attempt, and only the job's join of that attempt publishes it to the episode's
        // transcript location (JobCompletionService), so a run of an earlier attempt or of an ended job never touches a published one
        await _blobService.UploadPendingTranscriptAsync(request.FeedId, request.JobId, RunAttempt(request), vtt);

        var completed = await _jobService.MergeJobFieldsAsync(request.JobId, e =>
        {
            e.TranscriptionStatus = TranscriptionStatuses.Completed;
        }, ct, RunAttempt(request), TrackOpen);
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
        }, ct, RunAttempt(request), TrackOpen);
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

    /// <summary>
    /// Probe the source audio's duration through a read-only SAS URL, before anything is downloaded, so audio
    /// that goes to batch (which reads the blob by SAS itself) never touches local disk. Returns null when the
    /// SAS URL or the URL probe fails; the caller then downloads the blob and probes the local copy instead.
    /// </summary>
    private async Task<TimeSpan?> TryProbeSourceAsync(TranscriptionRequest request, CancellationToken ct)
    {
        try
        {
            var sasUrl = await GenerateSourceSasUrlAsync(request);

            return await _durationProbe.GetDurationAsync(new Uri(sasUrl), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log the exception type only: ffprobe echoes its input, so the message can carry the SAS token.
            _logger.LogWarning(
                "Probing {FileName} by URL failed ({ExceptionType}) for job {JobId}; downloading it to probe locally",
                request.FileName,
                ex.GetType().Name,
                request.JobId);

            return null;
        }
    }

    /// <summary>
    /// Read-only SAS URL of the run's source audio: the published episode audio for a transcript retry, the pending upload otherwise.
    /// </summary>
    private Task<string> GenerateSourceSasUrlAsync(TranscriptionRequest request) =>
        request.FromEpisodeAudio
            ? _blobService.GenerateAudioSasUrlAsync(request.FeedId, request.FileName)
            : _blobService.GeneratePendingBlobSasUrlAsync(request.FeedId, request.JobId, request.FileName);

    private async Task<string> DownloadSourceToTempAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var tempInputFile = Path.Combine(GetOrCreateTempDir(), $"{RunArtifactStem(request)}{Path.GetExtension(request.FileName)}");

        await using (var downloadStream = request.FromEpisodeAudio
            ? await _blobService.DownloadAudioAsync(request.FeedId, request.FileName)
            : await _blobService.DownloadPendingBlobAsync(request.FeedId, request.JobId, request.FileName))
        await using (var fileStream = File.Create(tempInputFile))
        {
            await downloadStream.CopyToAsync(fileStream, ct);
        }

        return tempInputFile;
    }

    /// <summary>
    /// Stem of a run's local temp files, scoped to its job and attempt so that runs of two attempts of one job never share a file.
    /// A process runs each attempt at most once (startup recovery resubmits only jobs a previous process queued).
    /// </summary>
    private static string RunArtifactStem(TranscriptionRequest request) => $"transcribe-{request.JobId}-a{RunAttempt(request)}";

    /// <summary>
    /// Convert <paramref name="tempInputFile"/> to 16 kHz mono WAV via <see cref="IWavConverter"/>.
    /// Returns the temp WAV path (a failed conversion leaves none behind). Pure local I/O -- no
    /// blob upload (callers do that separately for batch).
    /// </summary>
    private async Task<string> ConvertToWavAsync(string tempInputFile, TranscriptionRequest request, CancellationToken ct)
    {
        var jobId = request.JobId;
        var tempWavFile = Path.Combine(GetOrCreateTempDir(), $"{RunArtifactStem(request)}.wav");
        await _wavConverter.ConvertAsync(tempInputFile, tempWavFile, ct);

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
