namespace FeatherPod.Functions;

/// <summary>
/// Configuration settings for the Functions app.
/// </summary>
public class FunctionSettings
{
    public string StorageAccountName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "featherpod";
    public string? AppServiceUrl { get; set; }
    public string? InternalKey { get; set; }
    public int JobRetentionDays { get; set; } = 7;
    public int OrphanedBlobRetentionDays { get; set; } = 1;
    public string CleanupSchedule { get; set; } = "0 0 3 * * *";
}
