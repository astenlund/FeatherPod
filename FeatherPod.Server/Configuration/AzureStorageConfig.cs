namespace FeatherPod.Server.Configuration;

// Binds the "Azure" section of appsettings.json. One of ConnectionString or AccountName
// must be set: ConnectionString is used for local Azurite, AccountName for managed identity.
public record AzureStorageConfig
{
    public string ConnectionString { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "featherpod";
}
