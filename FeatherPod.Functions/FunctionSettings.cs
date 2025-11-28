namespace FeatherPod.Functions;

/// <summary>
/// Configuration settings for the Functions app.
/// </summary>
public record FunctionSettings
{
    public string StorageAccountName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "featherpod";
    public string? AppServiceUrl { get; set; }
    public string? InternalKey { get; set; }
}
