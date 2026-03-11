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
    public string Status { get; set; } = nameof(JobStatus.Queued);

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
    /// Current processing stage (Queued, Preparing, Analyzing, Normalizing, Finishing, Completed, Failed, Cancelled).
    /// </summary>
    public string? Stage { get; set; }

    /// <summary>
    /// Progress percentage within current stage (0-100).
    /// </summary>
    public int? ProgressPercent { get; set; }

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
    public static JobStatusEntity CreateQueued(string jobId, string feedId, string? fileName = null, string? progressMode = null, int? progressIntervalMs = null)
    {
        return new JobStatusEntity
        {
            PartitionKey = "jobs",
            RowKey = jobId,
            Status = nameof(JobStatus.Queued),
            Stage = nameof(NormalizationStage.Queued),
            FeedId = feedId,
            FileName = fileName,
            ProgressMode = progressMode,
            ProgressIntervalMs = progressIntervalMs,
            QueuedAt = DateTimeOffset.UtcNow,
            ProgressPercent = 0,
            ProgressMessage = "Waiting in queue"
        };
    }
}
