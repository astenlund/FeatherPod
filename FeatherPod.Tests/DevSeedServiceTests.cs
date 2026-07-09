using System.Text.Json;

using Azure.Storage.Blobs;

using FeatherPod.Infrastructure;
using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class DevSeedServiceTests : IDisposable
{
    private readonly string _testDirectory;

    public DevSeedServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodSeedTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuildSeededAdmin_MintsKeyInExpectedFormat()
    {
        // Arrange
        var existing = new UsersMetadata();

        // Act
        var (metadata, apiKey) = DevSeedService.BuildSeededAdmin(existing, "admin", "Admin", null);

        // Assert
        Assert.StartsWith("fp_admin_", apiKey);
        var admin = Assert.Single(metadata.Users);
        Assert.Equal("admin", admin.Id);
        Assert.Equal("Admin", admin.Name);
        Assert.Null(admin.Email);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Empty(admin.OwnedFeeds);
    }

    [Fact]
    public void BuildSeededAdmin_StoresVerifiableHash()
    {
        // Arrange
        var existing = new UsersMetadata();

        // Act
        var (metadata, apiKey) = DevSeedService.BuildSeededAdmin(existing, "admin", "Admin", "admin@example.com");

        // Assert - the persisted hash must recompute from the minted key + stored salt
        var admin = metadata.Users[0];
        Assert.NotNull(admin.ApiKeySalt);
        Assert.Equal(admin.ApiKeyHash, ApiKeyGenerator.Hash(apiKey, admin.ApiKeySalt!));
    }

    [Fact]
    public void BuildSeededAdmin_PreservesExistingUsers()
    {
        // Arrange
        var existing = new UsersMetadata
        {
            Users =
            [
                new User { Id = "existing", Name = "Existing", Role = UserRole.FeedOwner, ApiKeyHash = "hash" }
            ]
        };

        // Act
        var (metadata, _) = DevSeedService.BuildSeededAdmin(existing, "admin", "Admin", null);

        // Assert
        Assert.Equal(2, metadata.Users.Count);
        Assert.Contains(metadata.Users, u => u.Id == "existing");
        Assert.Contains(metadata.Users, u => u.Id == "admin" && u.Role == UserRole.Admin);
    }

    [Fact]
    public async Task SeededAdminKey_AuthenticatesThroughUserService()
    {
        // Arrange - seed an admin, then load the same users.json into the server's UserService
        var (metadata, apiKey) = DevSeedService.BuildSeededAdmin(new UsersMetadata(), "admin", "Admin", null);

        var blobStorage = new TestBlobStorageService(_testDirectory);
        var json = JsonSerializer.Serialize(metadata);
        await blobStorage.SaveUsersConfigAsync(json);

        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error)).CreateLogger<UserService>();
        using var userService = new UserService(blobStorage, logger);
        await userService.LoadUsersAsync();

        // Act
        var authenticated = await userService.GetUserByApiKeyAsync(apiKey);

        // Assert - the seeded key resolves to the admin through the real server auth path
        Assert.NotNull(authenticated);
        Assert.Equal("admin", authenticated.Id);
        Assert.Equal(UserRole.Admin, authenticated.Role);
    }
}

[Collection("Sequential")]
public class DevSeedServiceAzuriteTests : IAsyncLifetime
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _containerClient;
    private readonly string _containerName;

    public DevSeedServiceAzuriteTests()
    {
        _blobServiceClient = new BlobServiceClient("UseDevelopmentStorage=true");
        _containerName = $"test-seed-{Guid.NewGuid():N}";
        _containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _containerClient.DeleteIfExistsAsync();
    }

    [AzuriteFact]
    public async Task SeedAdminAsync_CreatesContainerAndWritesUsers()
    {
        // Act
        var result = await DevSeedService.SeedAdminAsync(_containerClient, "admin", "Admin", null);

        // Assert
        Assert.Equal(SeedOutcome.Created, result.Outcome);
        Assert.NotNull(result.ApiKey);
        Assert.StartsWith("fp_admin_", result.ApiKey);

        var download = await _containerClient.GetBlobClient(BlobPaths.UsersConfig).DownloadContentAsync();
        var metadata = JsonSerializer.Deserialize<UsersMetadata>(download.Value.Content.ToString());
        Assert.NotNull(metadata);
        var admin = Assert.Single(metadata.Users);
        Assert.Equal("admin", admin.Id);
        Assert.Equal(admin.ApiKeyHash, ApiKeyGenerator.Hash(result.ApiKey!, admin.ApiKeySalt!));
    }

    [AzuriteFact]
    public async Task SeedAdminAsync_IsIdempotent_WhenUserIdTaken()
    {
        // Arrange
        await DevSeedService.SeedAdminAsync(_containerClient, "admin", "Admin", null);

        // Act - a second seed of the same id must not clobber the store
        var result = await DevSeedService.SeedAdminAsync(_containerClient, "admin", "Admin", null);

        // Assert
        Assert.Equal(SeedOutcome.UserIdTaken, result.Outcome);
        Assert.Null(result.ApiKey);

        var download = await _containerClient.GetBlobClient(BlobPaths.UsersConfig).DownloadContentAsync();
        var metadata = JsonSerializer.Deserialize<UsersMetadata>(download.Value.Content.ToString());
        Assert.NotNull(metadata);
        Assert.Single(metadata.Users);
    }
}
