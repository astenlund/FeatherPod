namespace FeatherPod.Server.Models;

public record FeedsMetadata
{
    public List<FeedConfig> Feeds { get; init; } = [];
}
