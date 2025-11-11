using Azure.Storage.Blobs;
using Xunit;

namespace FeatherPod.Tests;

/// <summary>
/// Custom xUnit Fact attribute that skips tests if Azurite is not running.
/// Tests will be skipped with a helpful message instead of failing.
/// </summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> _isAzuriteAvailable = new(CheckAzuriteAvailability);

    public AzuriteFactAttribute()
    {
        if (!_isAzuriteAvailable.Value)
        {
            Skip = @"Azurite is not running. Start Azurite with: azurite --silent --location $env:USERPROFILE\.azurite --debug $env:USERPROFILE\.azurite\debug.log";
        }
    }

    private static bool CheckAzuriteAvailability()
    {
        try
        {
            // Try to connect to Azurite using the development storage connection string
            var blobServiceClient = new BlobServiceClient("UseDevelopmentStorage=true");

            // Attempt a real operation to verify Azurite is responding
            // Using a very short timeout to fail fast
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            var response = blobServiceClient.GetBlobContainersAsync(cancellationToken: cts.Token)
                .AsPages()
                .GetAsyncEnumerator(cts.Token);

            // Just try to get the first page - this will throw if Azurite isn't running
            var task = response.MoveNextAsync();
            return task.AsTask().Wait(1000) && task.Result;
        }
        catch
        {
            // Azurite is not running or not accessible
            return false;
        }
    }
}
