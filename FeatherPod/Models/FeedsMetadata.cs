namespace FeatherPod.Models;

public record FeedsMetadata
{
    public List<FeedConfig> Feeds { get; init; } = [];
}
