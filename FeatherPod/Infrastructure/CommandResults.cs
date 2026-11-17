using FeatherPod.Shared.Models;

namespace FeatherPod.Infrastructure;

public record FeedOperationResult
{
    public bool Success { get; init; }
    public string? FeedId { get; init; }
    public string? OldFeedId { get; init; }  // For renames
    public FeedConfig? Feed { get; init; }
    public string? ErrorMessage { get; init; }
}

public record EpisodeOperationResult
{
    public bool Success { get; init; }
    public string? EpisodeId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of requesting a transcript retry: the retry job's id when the server accepted it, otherwise the server's refusal
/// <see cref="Reason"/> (a machine-readable code such as <c>retry-active</c>) and <see cref="ErrorMessage"/>.
/// </summary>
public record TranscriptRetryRequestResult
{
    public bool Success { get; init; }
    public string? JobId { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
}
