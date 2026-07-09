using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace FeatherPod.Shared.Services;

/// <summary>
/// Single source of truth for the "connection string vs. managed identity" branching used to
/// construct Azure storage clients. A non-empty connection string wins (local Azurite / dev);
/// otherwise a named service URI is built and authenticated with <see cref="DefaultAzureCredential"/>.
/// When neither a connection string nor an account name is supplied, an
/// <see cref="InvalidOperationException"/> is thrown.
/// </summary>
public static class StorageClientFactory
{
    private const string EndpointSuffix = "core.windows.net";

    public static BlobServiceClient CreateBlobServiceClient(string? connectionString, string? accountName)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            return new BlobServiceClient(connectionString);
        }

        var blobUri = new Uri($"https://{RequireAccountName(accountName)}.blob.{EndpointSuffix}");

        return new BlobServiceClient(blobUri, new DefaultAzureCredential());
    }

    public static TableServiceClient CreateTableServiceClient(string? connectionString, string? accountName)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            return new TableServiceClient(connectionString);
        }

        var tableUri = new Uri($"https://{RequireAccountName(accountName)}.table.{EndpointSuffix}");

        return new TableServiceClient(tableUri, new DefaultAzureCredential());
    }

    public static TableClient CreateTableClient(string? connectionString, string? accountName, string tableName)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            return new TableClient(connectionString, tableName);
        }

        var tableUri = new Uri($"https://{RequireAccountName(accountName)}.table.{EndpointSuffix}");

        return new TableClient(tableUri, tableName, new DefaultAzureCredential());
    }

    public static QueueClient CreateQueueClient(string? connectionString, string? accountName, string queueName, QueueClientOptions? options = null)
    {
        if (!string.IsNullOrEmpty(connectionString))
        {
            return new QueueClient(connectionString, queueName, options);
        }

        var queueUri = new Uri($"https://{RequireAccountName(accountName)}.queue.{EndpointSuffix}/{queueName}");

        return new QueueClient(queueUri, new DefaultAzureCredential(), options);
    }

    private static string RequireAccountName(string? accountName)
    {
        if (string.IsNullOrEmpty(accountName))
        {
            throw new InvalidOperationException("Azure storage configuration requires either ConnectionString or AccountName");
        }

        return accountName;
    }
}
