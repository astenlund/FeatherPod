namespace FeatherPod.Models;

public class PodcastConfig
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool UseFileMetadataForPublishDate { get; set; } = false;
}

public class PathsConfig
{
    public string Episodes { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
}
