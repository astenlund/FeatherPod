namespace FeatherPod.Models;

public record FeedConfig
{
    required public string Id { get; init; }
    required public string Title { get; init; }
    public string? Description { get; init; }
    required public string Author { get; init; }
    public string? Email { get; init; }
    public string Language { get; init; } = "en";
    public string? Category { get; init; }
    public string? ImageUrl { get; init; }
    public bool UseFileMetadataForPublishDate { get; init; } = false;
    public string? ImageVersion { get; init; }
}

// Legacy - kept for backward compatibility with existing appsettings.json
public record PodcastConfig
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string ImageUrl { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public bool UseFileMetadataForPublishDate { get; init; } = false;
    public string? ImageVersion { get; init; }
}

public record PathsConfig
{
    public string Episodes { get; init; } = string.Empty;
    public string Metadata { get; init; } = string.Empty;
}
