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
    public string? AzureOpenAIEndpoint { get; set; }
    public string? WhisperDeployment { get; set; }
    public int WhisperChunkMinutes { get; set; } = 12; // keep in sync with appsettings.json AzureOpenAI:WhisperChunkMinutes
    public int WhisperOverlapSeconds { get; set; } = 30; // keep in sync with appsettings.json AzureOpenAI:WhisperOverlapSeconds
}
