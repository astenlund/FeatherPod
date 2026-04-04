using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using FFMpegCore;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

using static FeatherPod.Shared.Models.NormalizationStage;

namespace FeatherPod.Functions;

/// <summary>
/// Azure Function that processes audio normalization jobs from the queue.
/// Downloads, analyzes, and normalizes audio in a single invocation.
/// </summary>
public class NormalizationFunction
{
    private readonly BlobServiceClient _blobClient;
    private readonly TableServiceClient _tableClient;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelemetryClient _telemetryClient;
    private readonly FunctionSettings _settings;
    private readonly ILogger<NormalizationFunction> _logger;

    private const string TableName = "normalizationjobs";
    private const string QueueName = "normalization-jobs";
    private static readonly TimeSpan DefaultProgressThrottle = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public NormalizationFunction(
        BlobServiceClient blobClient,
        TableServiceClient tableClient,
        IAudioNormalizationService normalizationService,
        IHttpClientFactory httpClientFactory,
        TelemetryClient telemetryClient,
        IOptions<FunctionSettings> settings,
        ILogger<NormalizationFunction> logger)
    {
        _blobClient = blobClient;
        _tableClient = tableClient;
        _normalizationService = normalizationService;
        _httpClientFactory = httpClientFactory;
        _telemetryClient = telemetryClient;
        _settings = settings.Value;
        _logger = logger;
    }

    [Function("ProcessNormalizationJob")]
    public async Task ProcessNormalizationJob([QueueTrigger(QueueName, Connection = "AzureWebJobsStorage")] string? message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Queue trigger received message: {MessageLength} chars", message?.Length ?? 0);

        NormalizationJob job;
        try
        {
            job = JsonSerializer.Deserialize<NormalizationJob>(message!, JsonOptions) ?? throw new InvalidOperationException("Failed to deserialize normalization job");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize queue message. Raw message: {Message}", message);
            throw;
        }

        _logger.LogInformation("Processing normalization job {JobId} for {FeedId}/{FileName}",
            job.JobId, job.FeedId, job.FileName);
        _logger.LogDebug("Environment: HOME={Home}, TEMP={Temp}, FFmpegDir={FfmpegDir}",
            Environment.GetEnvironmentVariable("HOME"),
            Path.GetTempPath(),
            FFmpegBinaryManager.GetBinaryDirectory());
        _telemetryClient.Flush();

        var tableClient = _tableClient.GetTableClient(TableName);
        await tableClient.CreateIfNotExistsAsync(cancellationToken);

        // Check if the job was cancelled before we start processing
        var currentEntity = await GetJobEntityAsync(tableClient, job.JobId, cancellationToken);
        if (currentEntity?.GetJobStatus() == JobStatus.Cancelled)
        {
            _logger.LogInformation("Job {JobId} was cancelled before processing started, skipping", job.JobId);

            return;
        }

        // Transition to Processing with ETag-based optimistic concurrency
        if (!await TryTransitionToProcessingAsync(tableClient, job, currentEntity, cancellationToken))
        {
            return;
        }

        // Create a linked cancellation source that checks for cancellation in Table Storage
        using var cancellationCheckingCts = CreateCancellationCheckingSource(tableClient, job.JobId, cancellationToken);
        var linkedToken = cancellationCheckingCts.Token;

        // Automatic fallback: signalr → push → poll
        HubConnection? signalRConnection = null;
        var effectiveProgressMode = job.ProgressMode;

        if (effectiveProgressMode == "signalr")
        {
            signalRConnection = CreateSignalRConnection();
            if (signalRConnection != null)
            {
                try
                {
                    await signalRConnection.StartAsync(linkedToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SignalR connection failed for job {JobId}, falling back to push mode", job.JobId);
                    await signalRConnection.DisposeAsync();
                    signalRConnection = null;
                    effectiveProgressMode = "push";
                }
            }
            else
            {
                // No AppServiceUrl configured — can't use signalr or push
                effectiveProgressMode = null;
            }
        }

        if (effectiveProgressMode == "push" && string.IsNullOrEmpty(_settings.AppServiceUrl))
        {
            _logger.LogWarning("Push mode requested but AppServiceUrl not configured for job {JobId}, falling back to poll", job.JobId);
            effectiveProgressMode = null;
        }

        // Update job to use effective mode so all downstream code picks it up
        job = job with { ProgressMode = effectiveProgressMode };

        try
        {
            await ProcessNormalizationAsync(job, tableClient, linkedToken, signalRConnection);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelled via Table Storage flag (user cancellation), not host shutdown
            var entity = await GetJobEntityAsync(tableClient, job.JobId, CancellationToken.None);
            if (entity?.GetJobStatus() == JobStatus.Cancelled)
            {
                _logger.LogInformation("Job {JobId} was cancelled by user during processing, skipping", job.JobId);

                return;
            }

            // Not a user cancellation — re-throw so the Functions runtime can handle it
            throw;
        }
        finally
        {
            if (signalRConnection != null)
            {
                await signalRConnection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Downloads, analyzes, normalizes, uploads, and creates the episode in a single invocation.
    /// Progress mapping: Analyzing 0-35%, Normalizing 35-100%. Finishing is indeterminate.
    /// </summary>
    private async Task ProcessNormalizationAsync(NormalizationJob job, TableClient tableClient, CancellationToken cancellationToken, HubConnection? signalRConnection = null)
    {
        var containerClient = _blobClient.GetBlobContainerClient(_settings.ContainerName);
        var pendingBlobPath = $"{job.FeedId}/pending/{job.JobId}/{job.FileName}";
        var finalBlobPath = $"{job.FeedId}/audio/{job.FileName}";
        var progressThrottle = job.ProgressIntervalMs.HasValue ? TimeSpan.FromMilliseconds(job.ProgressIntervalMs.Value) : DefaultProgressThrottle;
        var progressMode = job.ProgressMode;

        string? tempInputFile = null;
        string? normalizedFile = null;

        try
        {
            // Download pending blob
            tempInputFile = await DownloadPendingBlobAsync(containerClient, pendingBlobPath, job, tableClient, Preparing, cancellationToken, signalRConnection);

            // Progress mapping: Analyzing 0-35%, Normalizing 35-100%. Finishing is indeterminate.
            var analyzeEnd = 35;
            var normalizeStart = analyzeEnd;
            var normalizeSize = 100 - analyzeEnd;

            // Analyze audio
            var inputFileSize = new FileInfo(tempInputFile).Length;
            _logger.LogInformation("Starting analysis for {FileName} (input size: {Size} bytes)", job.FileName, inputFileSize);
            _telemetryClient.Flush();

            var analyzeLastUpdate = DateTime.MinValue;
            Task? pendingAnalyzeUpdate = null;
            var analysisResult = await _normalizationService.AnalyzeAudioAsync(
                tempInputFile,
                progressCallback: progress =>
                {
                    var now = DateTime.UtcNow;
                    if (now - analyzeLastUpdate < progressThrottle)
                    {
                        return;
                    }
                    analyzeLastUpdate = now;

                    var mappedProgress = new ProgressUpdate
                    {
                        Stage = Analyzing,
                        ProgressPercent = progress.ProgressPercent * analyzeEnd / 100.0,
                        Message = progress.Message,
                        CurrentPosition = progress.CurrentPosition,
                        TotalDuration = progress.TotalDuration
                    };
                    _logger.LogDebug("Analyze progress: {Percent}% (mapped to {Mapped}%)", progress.ProgressPercent, mappedProgress.ProgressPercent);
                    pendingAnalyzeUpdate = UpdateProgressAsync(tableClient, job.JobId, mappedProgress, progressMode, signalRConnection)
                        .ContinueWith(t => _logger.LogError(t.Exception, "Analysis progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
                },
                cancellationToken);

            if (analysisResult == null)
            {
                throw new InvalidOperationException("Audio analysis failed - no result produced");
            }

            if (pendingAnalyzeUpdate != null)
            {
                try { await pendingAnalyzeUpdate; } catch { /* ignore */ }
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Analyzing,
                ProgressPercent = analyzeEnd,
                Message = "Analysis complete"
            }, progressMode, signalRConnection);

            _logger.LogInformation("Analysis complete for {FileName}: {InputLufs} LUFS, duration {Duration}",
                job.FileName, analysisResult.Analysis.InputI, analysisResult.Duration);

            // Normalize audio
            _logger.LogInformation("Starting normalization for {FileName}", job.FileName);
            _telemetryClient.Flush();

            var normalizeLastUpdate = DateTime.MinValue;
            Task? pendingNormalizeUpdate = null;
            normalizedFile = await _normalizationService.ApplyNormalizationAsync(
                tempInputFile,
                analysisResult.Analysis,
                analysisResult.Duration,
                progressCallback: progress =>
                {
                    var now = DateTime.UtcNow;
                    if (now - normalizeLastUpdate < progressThrottle)
                    {
                        return;
                    }
                    normalizeLastUpdate = now;

                    var mappedProgress = new ProgressUpdate
                    {
                        Stage = Normalizing,
                        ProgressPercent = normalizeStart + progress.ProgressPercent * normalizeSize / 100.0,
                        Message = progress.Message,
                        CurrentPosition = progress.CurrentPosition,
                        TotalDuration = progress.TotalDuration
                    };
                    _logger.LogDebug("Normalize progress: {Percent}% (mapped to {Mapped}%)", progress.ProgressPercent, mappedProgress.ProgressPercent);
                    pendingNormalizeUpdate = UpdateProgressAsync(tableClient, job.JobId, mappedProgress, progressMode, signalRConnection)
                        .ContinueWith(t => _logger.LogError(t.Exception, "Normalization progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
                },
                cancellationToken);

            if (normalizedFile == null)
            {
                throw new InvalidOperationException("Normalization failed - no output file produced");
            }

            if (pendingNormalizeUpdate != null)
            {
                try { await pendingNormalizeUpdate; } catch { /* ignore */ }
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Normalizing,
                ProgressPercent = normalizeStart + normalizeSize,
                Message = "Normalization complete"
            }, progressMode, signalRConnection);

            // Get duration from normalized file
            var mediaInfo = await FFProbe.AnalyseAsync(normalizedFile, cancellationToken: cancellationToken);
            var duration = mediaInfo.Duration;
            var normalizedFileSize = new FileInfo(normalizedFile).Length;

            // Upload normalized file
            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Finishing,
                ProgressPercent = 0,
                Message = "Finishing up"
            }, progressMode, signalRConnection);

            _logger.LogDebug("Uploading normalized file to {FinalPath}", finalBlobPath);
            var finalBlob = containerClient.GetBlobClient(finalBlobPath);
            var uploadLastUpdate = DateTime.MinValue;
            var uploadProgress = new Progress<long>(bytesUploaded =>
            {
                var now = DateTime.UtcNow;
                var percent = normalizedFileSize > 0 ? (double)bytesUploaded * 100 / normalizedFileSize : 0.0;
                if (percent < 100 && now - uploadLastUpdate < progressThrottle)
                {
                    return;
                }
                uploadLastUpdate = now;

                _logger.LogDebug("Upload progress callback: {Percent}%", percent);
                _ = UpdateProgressAsync(tableClient, job.JobId, new()
                {
                    Stage = Finishing,
                    ProgressPercent = percent,
                    Message = "Finishing up"
                }, progressMode, signalRConnection).ContinueWith(t => _logger.LogError(t.Exception, "Upload progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
            });

            await using (var stream = File.OpenRead(normalizedFile))
            {
                await finalBlob.UploadAsync(stream, new Azure.Storage.Blobs.Models.BlobUploadOptions
                {
                    ProgressHandler = uploadProgress
                }, cancellationToken);
            }

            await UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = Finishing,
                ProgressPercent = 100,
                Message = "Almost done"
            }, progressMode, signalRConnection);

            // Signal App Service that normalization succeeded (join logic creates the episode)
            await CallNormalizationCompleteAsync(job.JobId, new NormalizationCompleteRequest
            {
                Success = true,
                NormalizedFileSize = normalizedFileSize,
                AudioDurationMs = (long)duration.TotalMilliseconds
            }, cancellationToken);

            _logger.LogInformation("Normalization job {JobId} completed successfully, signaled App Service", job.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Normalization failed for job {JobId}: {ErrorType} - {ErrorMessage}",
                job.JobId, ex.GetType().Name, ex.Message);
            _telemetryClient.Flush();

            var sanitizedError = SanitizeErrorMessage(ex);

            // Signal App Service that normalization failed (join logic decides overall job status)
            try
            {
                await CallNormalizationCompleteAsync(job.JobId, new NormalizationCompleteRequest
                {
                    Success = false,
                    Error = sanitizedError
                }, cancellationToken);
            }
            catch (Exception signalEx)
            {
                // Safety net: if App Service is unreachable, write failure directly to Table Storage
                _logger.LogWarning(signalEx, "Failed to signal App Service for job {JobId}, falling back to direct write", job.JobId);
                var fallbackPartial = new JobStatusEntity
                {
                    PartitionKey = "jobs",
                    RowKey = job.JobId,
                    Status = nameof(JobStatus.Failed),
                    NormalizationComplete = true,
                    NormalizationError = sanitizedError,
                    Error = sanitizedError,
                    CompletedAt = DateTimeOffset.UtcNow
                };
                await tableClient.UpdateEntityAsync(fallbackPartial, Azure.ETag.All, TableUpdateMode.Merge, cancellationToken);
            }

            throw;
        }
        finally
        {
            CleanupTempFile(tempInputFile);
            CleanupTempFile(normalizedFile);
        }
    }

    /// <summary>
    /// Downloads the pending blob to a temp file with progress updates.
    /// </summary>
    private async Task<string> DownloadPendingBlobAsync(
        BlobContainerClient containerClient,
        string pendingBlobPath,
        NormalizationJob job,
        TableClient tableClient,
        NormalizationStage stage,
        CancellationToken cancellationToken,
        HubConnection? signalRConnection = null)
    {
        var pendingBlob = containerClient.GetBlobClient(pendingBlobPath);
        var progressMode = job.ProgressMode;

        var blobProperties = await pendingBlob.GetPropertiesAsync(cancellationToken: cancellationToken);
        var downloadSize = blobProperties.Value.ContentLength;
        var progressThrottle = job.ProgressIntervalMs.HasValue ? TimeSpan.FromMilliseconds(job.ProgressIntervalMs.Value) : DefaultProgressThrottle;

        await UpdateProgressAsync(tableClient, job.JobId, new()
        {
            Stage = stage,
            ProgressPercent = 0,
            Message = "Preparing audio file"
        }, progressMode, signalRConnection);

        var tempInputFile = Path.Combine(Path.GetTempPath(), $"{job.JobId}_{job.FileName}");

        _logger.LogDebug("Downloading pending blob to {TempFile}", tempInputFile);
        var downloadLastUpdate = DateTime.MinValue;
        var downloadProgress = new Progress<long>(bytesDownloaded =>
        {
            var now = DateTime.UtcNow;
            var percent = downloadSize > 0 ? (double)bytesDownloaded * 100 / downloadSize : 0.0;
            if (percent < 100 && now - downloadLastUpdate < progressThrottle)
            {
                return;
            }
            downloadLastUpdate = now;

            _logger.LogDebug("Download progress callback: {Percent}%", percent);
            _ = UpdateProgressAsync(tableClient, job.JobId, new()
            {
                Stage = stage,
                ProgressPercent = percent,
                Message = "Preparing audio file"
            }, progressMode, signalRConnection).ContinueWith(t => _logger.LogError(t.Exception, "Download progress update failed"), TaskContinuationOptions.OnlyOnFaulted);
        });

        await pendingBlob.DownloadToAsync(tempInputFile, new() { ProgressHandler = downloadProgress }, cancellationToken);

        return tempInputFile;
    }

    private static string SanitizeErrorMessage(Exception ex)
    {
        return ex switch
        {
            InvalidOperationException => ex.Message,
            FileNotFoundException => "Input file not found",
            IOException => "File processing error",
            _ => "An internal error occurred during audio normalization"
        };
    }

    /// <summary>
    /// Handle messages that have failed all retry attempts.
    /// </summary>
    [Function("ProcessPoisonNormalizationJob")]
    public async Task ProcessPoisonJob([QueueTrigger($"{QueueName}-poison", Connection = "AzureWebJobsStorage")] string message, CancellationToken cancellationToken)
    {
        var job = JsonSerializer.Deserialize<NormalizationJob>(message);
        if (job == null)
        {
            _logger.LogError("Failed to deserialize poison queue message");
            return;
        }

        _logger.LogWarning("Processing poison queue message for job {JobId}", job.JobId);

        var tableClient = _tableClient.GetTableClient(TableName);

        // Signal normalization failure to App Service (join logic decides overall job status)
        var error = "Job failed after maximum retry attempts";
        try
        {
            await CallNormalizationCompleteAsync(job.JobId, new NormalizationCompleteRequest
            {
                Success = false,
                Error = error
            }, cancellationToken);
        }
        catch (Exception signalEx)
        {
            // Safety net: if App Service is unreachable, write failure directly
            _logger.LogWarning(signalEx, "Failed to signal App Service for poison job {JobId}, falling back to direct write", job.JobId);
            var fallbackPartial = new JobStatusEntity
            {
                PartitionKey = "jobs",
                RowKey = job.JobId,
                Status = nameof(JobStatus.Failed),
                NormalizationComplete = true,
                NormalizationError = error,
                Error = error,
                CompletedAt = DateTimeOffset.UtcNow
            };
            await tableClient.UpdateEntityAsync(fallbackPartial, Azure.ETag.All, TableUpdateMode.Merge, cancellationToken);
        }

        _logger.LogInformation("Poison job {JobId} handled, signaled normalization failure", job.JobId);
    }

    private async Task UpdateJobStatusAsync(
        TableClient tableClient,
        string jobId,
        string feedId,
        JobStatus status,
        string? progressMode = null,
        HubConnection? signalRConnection = null,
        string? episodeId = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        // Read existing entity for terminal-state guard and ETag
        var existingResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);
        var entity = existingResponse.Value;

        // Don't overwrite a terminal state — first terminal state wins (e.g., user cancelled)
        var currentStatus = entity.GetJobStatus();
        if (currentStatus is JobStatus.Cancelled or JobStatus.Completed or JobStatus.Failed)
        {
            _logger.LogDebug("Skipping status update to {NewStatus} for job {JobId} — already in terminal state {Status}", status, jobId, currentStatus);

            return;
        }

        // Partial entity Merge — only write the fields we're changing
        var partial = new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = jobId,
            Status = status.ToString(),
            EpisodeId = episodeId,
            Error = error
        };

        if (status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            partial.CompletedAt = DateTimeOffset.UtcNow;
        }

        await tableClient.UpdateEntityAsync(partial, entity.ETag, TableUpdateMode.Merge, cancellationToken);

        // Apply partial fields to the read entity for push notifications
        entity.Status = partial.Status;
        entity.EpisodeId = partial.EpisodeId;
        entity.Error = partial.Error;
        if (partial.CompletedAt.HasValue)
        {
            entity.CompletedAt = partial.CompletedAt;
        }

        // Fire-and-forget push for terminal states
        if (progressMode == "push")
        {
            _ = PushProgressToServerAsync(jobId, entity);
        }
        else if (progressMode == "signalr" && signalRConnection != null)
        {
            // Await terminal states to ensure delivery before HubConnection disposal
            if (status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                await PushProgressViaSignalRAsync(signalRConnection, jobId, entity);
            }
            else
            {
                _ = PushProgressViaSignalRAsync(signalRConnection, jobId, entity);
            }
        }
    }

    private async Task UpdateProgressAsync(TableClient tableClient, string jobId, ProgressUpdate progress, string? progressMode = null, HubConnection? signalRConnection = null)
    {
        try
        {
            // Read existing entity to preserve QueuedAt and other fields
            var existingResponse = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId);
            var entity = existingResponse.Value;

            // Don't overwrite terminal states (e.g., user cancelled the job)
            var currentStatus = entity.GetJobStatus();
            if (currentStatus is JobStatus.Cancelled or JobStatus.Completed or JobStatus.Failed)
            {
                _logger.LogDebug("Skipping progress update for job {JobId} — already in terminal state {Status}", jobId, currentStatus);

                return;
            }

            // Partial entity Merge — only write normalization progress fields
            var partial = new JobStatusEntity
            {
                PartitionKey = "jobs",
                RowKey = jobId,
                NormalizationStage = progress.Stage.ToString(),
                NormalizationProgress = progress.ProgressPercent,
                ProgressMessage = progress.Message,
                CurrentPositionMs = progress.CurrentPosition.HasValue ? (long)progress.CurrentPosition.Value.TotalMilliseconds : null,
                Status = progress.Stage switch
                {
                    Completed => nameof(JobStatus.Completed),
                    Failed => nameof(JobStatus.Failed),
                    Cancelled => nameof(JobStatus.Cancelled),
                    Queued => nameof(JobStatus.Queued),
                    _ => nameof(JobStatus.Processing)
                }
            };

            // Only update TotalDurationMs if provided (preserve existing value from Normalizing stage)
            if (progress.TotalDuration.HasValue)
            {
                partial.TotalDurationMs = (long)progress.TotalDuration.Value.TotalMilliseconds;
            }

            if (progress.Stage is Completed or Failed or Cancelled)
            {
                partial.CompletedAt = DateTimeOffset.UtcNow;
            }

            await tableClient.UpdateEntityAsync(partial, entity.ETag, TableUpdateMode.Merge);
            _logger.LogDebug("Progress update saved: {Stage} {Percent}%", progress.Stage, progress.ProgressPercent);

            // Apply partial fields to the read entity for push notifications
            entity.NormalizationStage = partial.NormalizationStage;
            entity.NormalizationProgress = partial.NormalizationProgress;
            entity.ProgressMessage = partial.ProgressMessage;
            entity.CurrentPositionMs = partial.CurrentPositionMs;
            entity.Status = partial.Status;
            if (partial.TotalDurationMs.HasValue)
            {
                entity.TotalDurationMs = partial.TotalDurationMs;
            }

            if (partial.CompletedAt.HasValue)
            {
                entity.CompletedAt = partial.CompletedAt;
            }

            // Fire-and-forget push after successful Table Storage write
            if (progressMode == "push")
            {
                _ = PushProgressToServerAsync(jobId, entity);
            }
            else if (progressMode == "signalr" && signalRConnection != null)
            {
                // Await terminal stages to ensure delivery before HubConnection disposal
                if (progress.Stage is Completed or Failed or Cancelled)
                {
                    await PushProgressViaSignalRAsync(signalRConnection, jobId, entity);
                }
                else
                {
                    _ = PushProgressViaSignalRAsync(signalRConnection, jobId, entity);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // ETag conflict - entity was modified concurrently, ignore and continue
            _logger.LogDebug("Progress update conflict for job {JobId}, will retry on next update", jobId);
        }
        catch (RequestFailedException ex) when (ex.Status == 429)
        {
            _logger.LogWarning("Progress update throttled (429) for job {JobId}", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update progress for job {JobId}", jobId);
        }
    }

    private async Task PushProgressToServerAsync(string jobId, JobStatusEntity entity)
    {
        if (string.IsNullOrEmpty(_settings.AppServiceUrl))
        {
            return;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);

            if (!string.IsNullOrEmpty(_settings.InternalKey))
            {
                client.DefaultRequestHeaders.Add("X-Internal-Key", _settings.InternalKey);
            }

            var response = JobStatusResponse.FromEntity(entity);
            var json = JsonSerializer.Serialize(response, JsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            await client.PostAsync($"{_settings.AppServiceUrl}/api/internal/jobs/{jobId}/progress", content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push progress for job {JobId}", jobId);
        }
    }

    private HubConnection? CreateSignalRConnection()
    {
        if (string.IsNullOrEmpty(_settings.AppServiceUrl))
        {
            return null;
        }

        var hubUrl = $"{_settings.AppServiceUrl}/api/internal/signalrhub";
        if (!string.IsNullOrEmpty(_settings.InternalKey))
        {
            hubUrl += $"?key={Uri.EscapeDataString(_settings.InternalKey)}";
        }

        return new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    private async Task PushProgressViaSignalRAsync(HubConnection connection, string jobId, JobStatusEntity entity)
    {
        try
        {
            var response = JobStatusResponse.FromEntity(entity);
            await connection.SendAsync("SendProgress", jobId, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push progress via SignalR for job {JobId}", jobId);
        }
    }

    private async Task CallNormalizationCompleteAsync(string jobId, NormalizationCompleteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.AppServiceUrl))
        {
            throw new InvalidOperationException("AppServiceUrl not configured, cannot signal normalization completion");
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        if (!string.IsNullOrEmpty(_settings.InternalKey))
        {
            client.DefaultRequestHeaders.Add("X-Internal-Key", _settings.InternalKey);
        }

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{_settings.AppServiceUrl}/api/internal/jobs/{jobId}/normalization-complete", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Signaled normalization-complete for job {JobId} (success={Success})", jobId, request.Success);
    }

    private static async Task<JobStatusEntity?> GetJobEntityAsync(TableClient tableClient, string jobId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await tableClient.GetEntityAsync<JobStatusEntity>("jobs", jobId, cancellationToken: cancellationToken);

            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task<bool> TryTransitionToProcessingAsync(TableClient tableClient, NormalizationJob job, JobStatusEntity? entity, CancellationToken cancellationToken)
    {
        if (entity == null)
        {
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Processing, cancellationToken: cancellationToken);

            return true;
        }

        var partial = new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = job.JobId,
            Status = nameof(JobStatus.Processing)
        };

        try
        {
            await tableClient.UpdateEntityAsync(partial, entity.ETag, TableUpdateMode.Merge, cancellationToken);

            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // ETag conflict — re-read and check if cancelled
            var refreshed = await GetJobEntityAsync(tableClient, job.JobId, cancellationToken);
            if (refreshed?.GetJobStatus() == JobStatus.Cancelled)
            {
                _logger.LogInformation("Job {JobId} was cancelled during transition to Processing, skipping", job.JobId);

                return false;
            }

            // Not cancelled, fall back to unconditional upsert
            await UpdateJobStatusAsync(tableClient, job.JobId, job.FeedId, JobStatus.Processing, cancellationToken: cancellationToken);

            return true;
        }
    }

    private CancellationTokenSource CreateCancellationCheckingSource(TableClient tableClient, string jobId, CancellationToken parentToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!linkedCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token);
                    var entity = await GetJobEntityAsync(tableClient, jobId, CancellationToken.None);
                    if (entity?.GetJobStatus() == JobStatus.Cancelled)
                    {
                        _logger.LogInformation("Cancellation detected for job {JobId} via polling", jobId);
                        await linkedCts.CancelAsync();

                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the linked source is cancelled normally
            }
            catch (ObjectDisposedException)
            {
                // CTS was disposed before the polling loop exited
            }
        }, CancellationToken.None);

        return linkedCts;
    }

    private void CleanupTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

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
