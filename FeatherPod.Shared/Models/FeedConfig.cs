namespace FeatherPod.Shared.Models;

public record FeedConfig
{
    required public string Id { get; init; }
    required public string Title { get; init; }
    public string? Description { get; init; }
    public string? Summary { get; init; }
    required public string Author { get; init; }
    public string? Email { get; init; }
    public string Language { get; init; } = "en";
    public string? Category { get; init; }
    public bool UseFileMetadataForPublishDate { get; init; }
}
