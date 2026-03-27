namespace FeatherPod.Server.Models;

public record SuggestTitleRequest
{
    public string? Note { get; init; }
}

public record MoveEpisodeRequest
{
    public string? TargetFeedId { get; init; }
}

public record CopyEpisodeRequest
{
    public string? TargetFeedId { get; init; }
}

public record CreateUserRequest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Role { get; init; }
    public List<string>? OwnedFeeds { get; init; }
}

public record GrantFeedOwnershipRequest
{
    public string? FeedId { get; init; }
}

public record YouTubeImportRequest
{
    /// <summary>
    /// YouTube video URL.
    /// </summary>
    required public string Url { get; init; }

    /// <summary>
    /// "audio" or "video".
    /// </summary>
    required public string Format { get; init; }
}
