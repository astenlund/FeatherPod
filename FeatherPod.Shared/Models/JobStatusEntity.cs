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
    /// Partition key. Use "jobs" for simplicity, or feedId for high concurrency.
    /// </summary>
    public string PartitionKey { get; set; } = "jobs";

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
    /// Transcription progress percentage (0-100), audio-position-based.
    /// </summary>
    public double? TranscriptionProgress { get; set; }

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
    /// Set the status from enum.
    /// </summary>
    public void SetJobStatus(JobStatus status)
    {
        Status = status.ToString();
    }

    /// <summary>
    /// Create a new JobStatusEntity for a queued job.
    /// </summary>
    public static JobStatusEntity CreateQueued(
        string jobId,
        string feedId,
        string? fileName = null,
        string? title = null,
        string? progressMode = null,
        int? progressIntervalMs = null,
        string? description = null,
        string? summary = null,
        DateTimeOffset? publishedDate = null,
        string? source = null,
        long? originalFileSize = null,
        string? episodeId = null,
        string? transcriptionStatus = null)
    {
        return new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = jobId,
            Status = nameof(JobStatus.Queued),
            NormalizationStage = nameof(Models.NormalizationStage.Queued),
            FeedId = feedId,
            FileName = fileName,
            Title = title,
            EpisodeId = episodeId,
            ProgressMode = progressMode,
            ProgressIntervalMs = progressIntervalMs,
            QueuedAt = DateTimeOffset.UtcNow,
            NormalizationProgress = 0,
            ProgressMessage = "Waiting in queue",
            Description = description,
            Summary = summary,
            PublishedDate = publishedDate,
            Source = source,
            OriginalFileSize = originalFileSize,
            TranscriptionStatus = transcriptionStatus
        };
    }
}
