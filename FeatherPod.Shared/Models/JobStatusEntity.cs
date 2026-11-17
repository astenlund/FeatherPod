using Azure;
using Azure.Data.Tables;

namespace FeatherPod.Shared.Models;

/// <summary>
/// Azure Table Storage entity for tracking normalization job status.
/// Provides concurrent write safety and better querying than JSON blob storage.
/// </summary>
public class JobStatusEntity : ITableEntity
{
    /// <summary>
    /// Partition key. Always <see cref="JobStorageNames.JobsPartitionKey"/> for the
    /// <see cref="JobStorageNames.TableName"/> table.
    /// </summary>
    public string PartitionKey { get; set; } = JobStorageNames.JobsPartitionKey;

    /// <summary>
    /// Row key. The JobId (GUID).
    /// </summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the job.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// The Episode ID once created (only set when Completed).
    /// </summary>
    public string? EpisodeId { get; set; }

    /// <summary>
    /// The Feed ID this job belongs to.
    /// </summary>
    public string? FeedId { get; set; }

    /// <summary>
    /// Error message if the job failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Timestamp when the job was queued.
    /// </summary>
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>
    /// Timestamp when the job completed or failed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Current normalization stage (Queued, Preparing, Analyzing, Normalizing, Finishing, Completed, Failed, Cancelled).
    /// Renamed from Stage for disambiguation in the parallel model.
    /// </summary>
    public string? NormalizationStage { get; set; }

    /// <summary>
    /// Normalization progress percentage (0-100).
    /// Renamed from ProgressPercent for disambiguation in the parallel model.
    /// </summary>
    public double? NormalizationProgress { get; set; }

    /// <summary>
    /// Whether normalization has completed (success or failure). Set by Function via normalization-complete endpoint.
    /// </summary>
    public bool? NormalizationComplete { get; set; }

    /// <summary>
    /// Error message if normalization failed.
    /// </summary>
    public string? NormalizationError { get; set; }

    /// <summary>
    /// Post-normalization file size in bytes.
    /// </summary>
    public long? NormalizedFileSize { get; set; }

    /// <summary>
    /// Audio duration in milliseconds (from FFprobe, set by Function on success).
    /// </summary>
    public long? AudioDurationMs { get; set; }

    /// <summary>
    /// Transcription track status: null (disabled), Queued, Running, Completed, Failed.
    /// </summary>
    public string? TranscriptionStatus { get; set; }

    /// <summary>
    /// Error message if transcription failed.
    /// </summary>
    public string? TranscriptionError { get; set; }

    /// <summary>
    /// When transcription started (for velocity calculation and stale job detection).
    /// </summary>
    public DateTimeOffset? TranscriptionStartedAt { get; set; }

    /// <summary>
    /// Human-readable progress message.
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// Current audio position in milliseconds (for progress display).
    /// </summary>
    public long? CurrentPositionMs { get; set; }

    /// <summary>
    /// Total audio duration in milliseconds (for progress display).
    /// </summary>
    public long? TotalDurationMs { get; set; }

    /// <summary>
    /// Original file name of the uploaded audio file.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Episode title (AI-generated or from YouTube metadata). Set at job creation time.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Episode description (carried from upload for join-time episode creation).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Episode summary.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Episode publish date.
    /// </summary>
    public DateTimeOffset? PublishedDate { get; set; }

    /// <summary>
    /// Upload source (Browser, CLI).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Pre-normalization file size (for episode ID verification).
    /// </summary>
    public long? OriginalFileSize { get; set; }

    /// <summary>
    /// Progress delivery mode: "poll", "push", or "signalr" (null = poll).
    /// </summary>
    public string? ProgressMode { get; set; }

    /// <summary>
    /// Progress update throttle interval in milliseconds (null = 500).
    /// </summary>
    public int? ProgressIntervalMs { get; set; }

    /// <summary>
    /// The ID the uploading client generated for this upload, so that it can recognize the job when the upload's
    /// response never reached it (null when the client sent none, as the CLI does).
    /// </summary>
    public string? ClientUploadId { get; set; }

    /// <summary>
    /// The job's current attempt: 1 for its first run, incremented by every normalization retry reset. Queue messages, normalization
    /// signals and transcription runs carry the attempt they were started for, and only work of the current attempt may change the job.
    /// Null for a job created before attempts existed, which is its first attempt (see <see cref="GetAttempt"/>).
    /// </summary>
    public int? Attempt { get; set; }

    /// <summary>
    /// What the job does (a <see cref="JobKinds"/> value); null for an upload job.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// For a transcript retry, the upload time of the episode incarnation it was started on; its join publishes only to that
    /// incarnation, never to an episode deleted and re-created under the same id meanwhile.
    /// </summary>
    public DateTimeOffset? EpisodeUploadedAt { get; set; }

    /// <summary>
    /// When the scheduled cleanup claimed this job's pending source for deletion (a conditional write on the job, so a normalization
    /// retry reset based on an earlier read loses its race). A retry refuses a job whose source was claimed.
    /// </summary>
    public DateTimeOffset? SourceSweptAt { get; set; }

    /// <summary>
    /// When a transcript retry's join claimed the job (a conditional write that leaves the job non-terminal while the join publishes).
    /// A join that finds the claim returns; the job's terminal status is written once, after publishing, with the actual outcome.
    /// </summary>
    public DateTimeOffset? JoinClaimedAt { get; set; }

    /// <summary>
    /// Azure Table Storage timestamp (managed by the service).
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Azure Table Storage ETag for optimistic concurrency.
    /// </summary>
    public ETag ETag { get; set; }

    /// <summary>
    /// Get the JobStatus enum value.
    /// </summary>
    public JobStatus GetJobStatus()
    {
        return Enum.TryParse<JobStatus>(Status, out var status) ? status : JobStatus.Queued;
    }

    /// <summary>
    /// The job's current attempt, reading a missing value as the first attempt.
    /// </summary>
    public int GetAttempt() => Attempt ?? 1;

    /// <summary>
    /// Whether work started for <paramref name="attempt"/> (null meaning the first attempt, as on messages sent before attempts existed)
    /// belongs to this job's current attempt.
    /// </summary>
    public bool IsCurrentAttempt(int? attempt) => (attempt ?? 1) == GetAttempt();

    /// <summary>
    /// Set the status from enum.
    /// </summary>
    public void SetJobStatus(JobStatus status)
    {
        Status = status.ToString();
    }

    /// <summary>
    /// Create a new JobStatusEntity for a queued job.
    /// </summary>
    public static JobStatusEntity CreateQueued(CreateJobOptions options)
    {
        var transcriptRetry = JobKinds.IsTranscriptRetry(options.Kind);

        return new JobStatusEntity
        {
            PartitionKey = JobStorageNames.JobsPartitionKey,
            RowKey = options.JobId,
            Status = nameof(JobStatus.Queued),
            Attempt = 1,
            Kind = options.Kind,
            EpisodeUploadedAt = options.EpisodeUploadedAt,

            // A transcript retry has no normalization track: it starts terminal and successful, so its join waits on transcription alone
            NormalizationStage = transcriptRetry ? nameof(Models.NormalizationStage.Completed) : nameof(Models.NormalizationStage.Queued),
            NormalizationComplete = transcriptRetry ? true : null,
            FeedId = options.FeedId,
            FileName = options.FileName,
            Title = options.Title,
            EpisodeId = options.EpisodeId,
            ProgressMode = options.ProgressMode,
            ProgressIntervalMs = options.ProgressIntervalMs,
            QueuedAt = DateTimeOffset.UtcNow,
            NormalizationProgress = transcriptRetry ? 100 : 0,
            ProgressMessage = transcriptRetry ? "Waiting to transcribe" : "Waiting in queue",
            Description = options.Description,
            Summary = options.Summary,
            PublishedDate = options.PublishedDate,
            Source = options.Source,
            OriginalFileSize = options.OriginalFileSize,
            TranscriptionStatus = options.TranscriptionStatus,
            ClientUploadId = options.ClientUploadId
        };
    }
}
