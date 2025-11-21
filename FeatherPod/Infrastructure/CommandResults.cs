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
