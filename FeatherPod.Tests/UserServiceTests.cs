using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FeatherPod.Models;
using FeatherPod.Services;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class UserServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ILogger<UserService> _logger;
    private readonly IConfiguration _configuration;
    private readonly List<UserService> _servicesToDispose = [];

    public UserServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Error);
        });

        _logger = loggerFactory.CreateLogger<UserService>();

        // Create minimal config (no legacy API key for most tests)
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>()!)
            .Build();
    }

    private UserService CreateService(IConfiguration? config = null)
    {
        var blobStorage = new TestBlobStorageService(_testDirectory);
        var service = new UserService(blobStorage, config ?? _configuration, _logger);
        _servicesToDispose.Add(service);
        return service;
    }

    public void Dispose()
    {
        foreach (var service in _servicesToDispose)
        {
            service.Dispose();
        }

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task CreateUserAsync_ShouldCreateNewUser()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = ["feed1"],
            ApiKeyHash = "", // Will be generated
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Act
        var apiKey = await service.CreateUserAsync(user);

        // Assert
        Assert.NotNull(apiKey);
        Assert.NotEmpty(apiKey);

        var retrieved = await service.GetUserByIdAsync("testuser");
        Assert.NotNull(retrieved);
        Assert.Equal("testuser", retrieved.Id);
        Assert.Equal("Test User", retrieved.Name);
        Assert.Equal("test@example.com", retrieved.Email);
        Assert.Equal(UserRole.FeedOwner, retrieved.Role);
        Assert.Single(retrieved.OwnedFeeds);
        Assert.Contains("feed1", retrieved.OwnedFeeds);
        Assert.True(retrieved.IsActive);
    }

    [Fact]
    public async Task CreateUserAsync_ShouldRejectDuplicateUserId()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user1 = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user1);

        var user2 = new User
        {
            Id = "testuser",
            Name = "Another User",
            Email = "another@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.CreateUserAsync(user2));
    }

    [Fact]
    public async Task GetUserByIdAsync_ShouldReturnUser_WhenExists()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        // Act
        var retrieved = await service.GetUserByIdAsync("testuser");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("testuser", retrieved.Id);
        Assert.Equal("Test User", retrieved.Name);
    }

    [Fact]
    public async Task GetUserByIdAsync_ShouldReturnNull_WhenNotExists()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var retrieved = await service.GetUserByIdAsync("nonexistent");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_ShouldReturnUser_WhenValidKey()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var apiKey = await service.CreateUserAsync(user);

        // Act
        var retrieved = await service.GetUserByApiKeyAsync(apiKey);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("testuser", retrieved.Id);
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_ShouldReturnNull_WhenInvalidKey()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var retrieved = await service.GetUserByApiKeyAsync("invalid-key");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task GetUserByApiKeyAsync_ShouldReturnNull_WhenUserInactive()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var apiKey = await service.CreateUserAsync(user);
        await service.DeleteUserAsync("testuser"); // Marks as inactive

        // Act
        var retrieved = await service.GetUserByApiKeyAsync(apiKey);

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteUserAsync_ShouldMarkUserInactive()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var apiKey = await service.CreateUserAsync(user);

        // Act
        var success = await service.DeleteUserAsync("testuser");

        // Assert
        Assert.True(success);

        // GetUserByIdAsync filters by IsActive, so deleted user should not be returned
        var retrieved = await service.GetUserByIdAsync("testuser");
        Assert.Null(retrieved);

        // API key should also not work anymore
        var retrievedByKey = await service.GetUserByApiKeyAsync(apiKey);
        Assert.Null(retrievedByKey);

        // User should not appear in active users list
        var allUsers = await service.GetAllUsersAsync();
        Assert.DoesNotContain(allUsers, u => u.Id == "testuser");
    }

    [Fact]
    public async Task DeleteUserAsync_ShouldReturnFalse_WhenUserNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var success = await service.DeleteUserAsync("nonexistent");

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task RegenerateApiKeyAsync_ShouldCreateNewKey()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var oldApiKey = await service.CreateUserAsync(user);

        // Act
        var newApiKey = await service.RegenerateApiKeyAsync("testuser");

        // Assert
        Assert.NotNull(newApiKey);
        Assert.NotEqual(oldApiKey, newApiKey);

        // Old key should no longer work
        var retrievedWithOldKey = await service.GetUserByApiKeyAsync(oldApiKey);
        Assert.Null(retrievedWithOldKey);

        // New key should work
        var retrievedWithNewKey = await service.GetUserByApiKeyAsync(newApiKey);
        Assert.NotNull(retrievedWithNewKey);
        Assert.Equal("testuser", retrievedWithNewKey.Id);
    }

    [Fact]
    public async Task RegenerateApiKeyAsync_ShouldReturnNull_WhenUserNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var newApiKey = await service.RegenerateApiKeyAsync("nonexistent");

        // Assert
        Assert.Null(newApiKey);
    }

    [Fact]
    public async Task GrantFeedOwnershipAsync_ShouldAddFeedToOwnedFeeds()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        // Act
        var success = await service.GrantFeedOwnershipAsync("testuser", "new-feed");

        // Assert
        Assert.True(success);
        var retrieved = await service.GetUserByIdAsync("testuser");
        Assert.NotNull(retrieved);
        Assert.Contains("new-feed", retrieved.OwnedFeeds);
    }

    [Fact]
    public async Task GrantFeedOwnershipAsync_ShouldNotDuplicate_WhenFeedAlreadyOwned()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = ["existing-feed"],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        // Act
        var success = await service.GrantFeedOwnershipAsync("testuser", "existing-feed");

        // Assert
        Assert.True(success);
        var retrieved = await service.GetUserByIdAsync("testuser");
        Assert.NotNull(retrieved);
        Assert.Single(retrieved.OwnedFeeds, f => f == "existing-feed");
    }

    [Fact]
    public async Task GrantFeedOwnershipAsync_ShouldReturnFalse_WhenUserNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var success = await service.GrantFeedOwnershipAsync("nonexistent", "some-feed");

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task RevokeFeedOwnershipAsync_ShouldRemoveFeedFromOwnedFeeds()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = ["feed1", "feed2"],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        // Act
        var success = await service.RevokeFeedOwnershipAsync("testuser", "feed1");

        // Assert
        Assert.True(success);
        var retrieved = await service.GetUserByIdAsync("testuser");
        Assert.NotNull(retrieved);
        Assert.DoesNotContain("feed1", retrieved.OwnedFeeds);
        Assert.Contains("feed2", retrieved.OwnedFeeds);
    }

    [Fact]
    public async Task RevokeFeedOwnershipAsync_ShouldReturnFalse_WhenUserNotFound()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        // Act
        var success = await service.RevokeFeedOwnershipAsync("nonexistent", "some-feed");

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task ValidatePermissionAsync_Admin_ShouldAccessAllFeeds()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var admin = new User
        {
            Id = "admin",
            Name = "Admin User",
            Email = "admin@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(admin);
        var adminUser = await service.GetUserByIdAsync("admin");

        // Act & Assert
        Assert.True(await service.ValidatePermissionAsync(adminUser!, "any-feed"));
        Assert.True(await service.ValidatePermissionAsync(adminUser!, "another-feed"));
    }

    [Fact]
    public async Task ValidatePermissionAsync_FeedOwner_ShouldOnlyAccessOwnedFeeds()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var owner = new User
        {
            Id = "owner",
            Name = "Feed Owner",
            Email = "owner@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = ["owned-feed"],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(owner);
        var ownerUser = await service.GetUserByIdAsync("owner");

        // Act & Assert
        Assert.True(await service.ValidatePermissionAsync(ownerUser!, "owned-feed"));
        Assert.False(await service.ValidatePermissionAsync(ownerUser!, "other-feed"));
    }

    [Fact]
    public async Task ValidatePermissionAsync_FeedOwner_NoFeeds_ShouldNotAccessAnyFeed()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var owner = new User
        {
            Id = "owner",
            Name = "Feed Owner",
            Email = "owner@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(owner);
        var ownerUser = await service.GetUserByIdAsync("owner");

        // Act & Assert
        Assert.False(await service.ValidatePermissionAsync(ownerUser!, "any-feed"));
    }

    [Fact]
    public async Task LoadUsersAsync_ShouldMigrateLegacyApiKey()
    {
        // Arrange
        var configData = new Dictionary<string, string>
        {
            ["ApiKey"] = "legacy-test-key"
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();

        var service = CreateService(config);

        // Act
        await service.LoadUsersAsync();

        // Assert - Admin user should be created
        var admin = await service.GetUserByIdAsync("admin");
        Assert.NotNull(admin);
        Assert.Equal("admin", admin.Id);
        Assert.Equal("Administrator", admin.Name);
        Assert.Equal(UserRole.Admin, admin.Role);

        // Legacy key should work
        var retrievedByKey = await service.GetUserByApiKeyAsync("legacy-test-key");
        Assert.NotNull(retrievedByKey);
        Assert.Equal("admin", retrievedByKey.Id);
    }

    [Fact]
    public async Task LoadUsersAsync_ShouldNotMigrate_WhenNoLegacyKey()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.LoadUsersAsync();

        // Assert - No admin user should be created
        var users = await service.GetAllUsersAsync();
        Assert.Empty(users);
    }

    [Fact]
    public async Task LoadUsersAsync_ShouldNotMigrate_WhenUsersAlreadyExist()
    {
        // Arrange
        var configData = new Dictionary<string, string>
        {
            ["ApiKey"] = "legacy-test-key"
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();

        var service = CreateService(config);
        await service.LoadUsersAsync();

        // Create another user
        var user = new User
        {
            Id = "user1",
            Name = "User One",
            Email = "user1@example.com",
            Role = UserRole.FeedOwner,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        // Create new service instance with same storage
        var service2 = CreateService(config);

        // Act
        await service2.LoadUsersAsync();

        // Assert - Should still have both users (admin from migration + user1)
        var users = await service2.GetAllUsersAsync();
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public async Task UpdateLastActiveAsync_ShouldUpdateTimestamp()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user = new User
        {
            Id = "testuser",
            Name = "Test User",
            Email = "test@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user);

        var originalUser = await service.GetUserByIdAsync("testuser");
        var originalLastActive = originalUser!.LastActive;

        // Wait to ensure timestamp difference
        await Task.Delay(100);

        // Act
        await service.UpdateLastActiveAsync("testuser");

        // Assert
        var updated = await service.GetUserByIdAsync("testuser");
        Assert.NotNull(updated);
        Assert.NotNull(updated.LastActive);

        // LastActive should be updated (note: UpdateLastActiveAsync doesn't save to blob, just updates in-memory)
        if (originalLastActive.HasValue)
        {
            Assert.True(updated.LastActive > originalLastActive);
        }
        else
        {
            Assert.NotNull(updated.LastActive);
        }
    }

    [Fact]
    public async Task GetAllUsersAsync_ShouldReturnOnlyActiveUsers()
    {
        // Arrange
        var service = CreateService();
        await service.LoadUsersAsync();

        var user1 = new User
        {
            Id = "user1",
            Name = "User One",
            Email = "user1@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user1);

        var user2 = new User
        {
            Id = "user2",
            Name = "User Two",
            Email = "user2@example.com",
            Role = UserRole.Admin,
            OwnedFeeds = [],
            ApiKeyHash = "",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await service.CreateUserAsync(user2);
        await service.DeleteUserAsync("user2"); // Mark inactive

        // Act
        var users = await service.GetAllUsersAsync();

        // Assert
        Assert.Single(users);
        Assert.Equal("user1", users[0].Id);
    }
}
