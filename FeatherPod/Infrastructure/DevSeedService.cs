using System.Text.Json;

using Azure;
using Azure.Storage.Blobs;

using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Outcome of a dev-seed attempt. <see cref="Created"/> carries a freshly minted key;
/// <see cref="UserIdTaken"/> means the store already had that user and was left untouched.
/// </summary>
internal enum SeedOutcome
{
    Created,
    UserIdTaken
}

internal sealed record SeedResult(SeedOutcome Outcome, string UserId, string? ApiKey);

/// <summary>
/// Bootstraps a fresh environment with an admin user by writing <c>users.json</c> directly
/// to blob storage. This is the sanctioned exception to the CLI's HTTP-API-only rule: user
/// creation normally requires an authenticated admin, so a brand-new store has no way to mint
/// the first one. Mirrors what the integration test harness does when it pre-seeds a test admin.
/// </summary>
internal static class DevSeedService
{
    internal const string DefaultConnectionString = "UseDevelopmentStorage=true";
    internal const string DefaultContainer = "featherpod";
    internal const string DefaultUserId = "admin";
    internal const string DefaultName = "Admin";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Ensures the container exists, then appends a new admin user to <c>users.json</c> and
    /// persists it. If a user with <paramref name="userId"/> already exists the store is left
    /// untouched (the existing plaintext key cannot be recovered), and the caller is expected to
    /// rotate or delete instead.
    /// </summary>
    internal static async Task<SeedResult> SeedAdminAsync(BlobContainerClient container, string userId, string name, string? email, CancellationToken cancellationToken = default)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var metadata = await LoadUsersAsync(container, cancellationToken);

        if (metadata.Users.Any(u => u.Id == userId))
        {
            return new SeedResult(SeedOutcome.UserIdTaken, userId, null);
        }

        var (updated, apiKey) = BuildSeededAdmin(metadata, userId, name, email);

        var json = JsonSerializer.Serialize(updated, JsonOptions);
        var usersBlob = container.GetBlobClient(BlobPaths.UsersConfig);
        await usersBlob.UploadAsync(BinaryData.FromString(json), overwrite: true, cancellationToken);

        return new SeedResult(SeedOutcome.Created, userId, apiKey);
    }

    /// <summary>
    /// Pure core: mints an admin key and returns the metadata that would be persisted, without
    /// touching blob storage. Split out so the crypto and metadata shape can be unit tested
    /// without a live Azurite instance.
    /// </summary>
    internal static (UsersMetadata Metadata, string ApiKey) BuildSeededAdmin(UsersMetadata existing, string userId, string name, string? email)
    {
        var (apiKey, salt) = ApiKeyGenerator.Generate(userId);
        var hash = ApiKeyGenerator.Hash(apiKey, salt);

        var admin = new User
        {
            Id = userId,
            Name = name,
            Email = email,
            Role = UserRole.Admin,
            ApiKeyHash = hash,
            ApiKeySalt = salt,
            OwnedFeeds = [],
            CreatedAt = DateTime.UtcNow
        };

        var updated = existing with { Users = [.. existing.Users, admin] };

        return (updated, apiKey);
    }

    private static async Task<UsersMetadata> LoadUsersAsync(BlobContainerClient container, CancellationToken cancellationToken)
    {
        var usersBlob = container.GetBlobClient(BlobPaths.UsersConfig);

        try
        {
            var download = await usersBlob.DownloadContentAsync(cancellationToken);

            return JsonSerializer.Deserialize<UsersMetadata>(download.Value.Content.ToString()) ?? new UsersMetadata();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new UsersMetadata();
        }
    }
}
