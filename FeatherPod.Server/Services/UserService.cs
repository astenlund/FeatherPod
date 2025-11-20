using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherPod.Server.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Service for managing users and their permissions.
/// Thread-safe singleton service using SemaphoreSlim.
/// </summary>
public sealed class UserService : IUserService, IDisposable
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<UserService> _logger;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    private UsersMetadata _usersMetadata = new();

    public UserService(IBlobStorageService blobStorage, IConfiguration configuration, ILogger<UserService> logger)
    {
        _blobStorage = blobStorage;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task LoadUsersAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = await _blobStorage.LoadUsersConfigAsync();
            if (json != null)
            {
                _usersMetadata = JsonSerializer.Deserialize<UsersMetadata>(json) ?? new UsersMetadata();
                _logger.LogInformation("Loaded {Count} users from blob storage", _usersMetadata.Users.Count);
            }
            else
            {
                _logger.LogInformation("No users.json found in blob storage.");

                // Check for legacy API key to migrate
                var legacyApiKey = _configuration["ApiKey"];
                if (!string.IsNullOrEmpty(legacyApiKey))
                {
                    _logger.LogWarning("Legacy API key detected. Migrating to user-based authentication...");

                    // Create admin user with legacy API key
                    var adminUser = new User
                    {
                        Id = "admin",
                        Name = "Administrator",
                        Email = "admin@featherpod.local",
                        Role = UserRole.Admin,
                        ApiKeyHash = HashApiKey(legacyApiKey),
                        OwnedFeeds = [],
                        CreatedAt = DateTime.UtcNow
                    };

                    _usersMetadata = new()
                    {
                        Users = [adminUser]
                    };

                    await SaveUsersAsync();

                    _logger.LogWarning("Migrated legacy API key to admin user 'admin'. " +
                        "The existing API key will continue to work, but consider rotating to a new key using the user management API.");
                }
                else
                {
                    _logger.LogInformation("No legacy API key found. Starting with empty user list.");
                    _usersMetadata = new();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<User>> GetAllUsersAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return _usersMetadata.Users.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<User?> GetUserByIdAsync(string userId)
    {
        await _lock.WaitAsync();
        try
        {
            return _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<User?> GetUserByApiKeyAsync(string apiKey)
    {
        var keyHash = HashApiKey(apiKey);

        await _lock.WaitAsync();
        try
        {
            return _usersMetadata.Users.FirstOrDefault(u => u.ApiKeyHash == keyHash);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> CreateUserAsync(User user)
    {
        // Generate API key
        var apiKey = GenerateApiKey();
        var keyHash = HashApiKey(apiKey);

        await _lock.WaitAsync();
        try
        {
            // Check if user already exists
            if (_usersMetadata.Users.Any(u => u.Id == user.Id))
            {
                throw new InvalidOperationException($"User with ID '{user.Id}' already exists.");
            }

            // Create user with hashed key
            var newUser = user with
            {
                ApiKeyHash = keyHash,
                CreatedAt = DateTime.UtcNow
            };

            _usersMetadata = _usersMetadata with
            {
                Users = [.. _usersMetadata.Users, newUser]
            };

            await SaveUsersAsync();

            _logger.LogInformation("Created user {UserId} with role {Role}", user.Id, user.Role);

            return apiKey; // Return plaintext key - only time it's visible
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteUserAsync(string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
            {
                return false;
            }

            _usersMetadata = _usersMetadata with
            {
                Users = _usersMetadata.Users.Where(u => u.Id != userId).ToList()
            };

            await SaveUsersAsync();

            _logger.LogInformation("Deleted user {UserId}", userId);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> RegenerateApiKeyAsync(string userId)
    {
        var newApiKey = GenerateApiKey();
        var newKeyHash = HashApiKey(newApiKey);

        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
            {
                return null;
            }

            var updatedUser = user with { ApiKeyHash = newKeyHash };

            _usersMetadata = _usersMetadata with
            {
                Users = _usersMetadata.Users.Select(u => u.Id == userId ? updatedUser : u).ToList()
            };

            await SaveUsersAsync();

            _logger.LogInformation("Regenerated API key for user {UserId}", userId);

            return newApiKey; // Return plaintext key - only time it's visible
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateLastActiveAsync(string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
            {
                return;
            }

            var updatedUser = user with { LastActive = DateTime.UtcNow };

            _usersMetadata = _usersMetadata with
            {
                Users = _usersMetadata.Users.Select(u => u.Id == userId ? updatedUser : u).ToList()
            };

            // Don't save to blob on every request - just update in-memory
            // This will be persisted on next save operation
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> GrantFeedOwnershipAsync(string userId, string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null || user.Role != UserRole.FeedOwner)
            {
                return false;
            }

            if (user.OwnedFeeds.Contains(feedId))
            {
                return true; // Already owns this feed
            }

            var updatedUser = user with
            {
                OwnedFeeds = [.. user.OwnedFeeds, feedId]
            };

            _usersMetadata = _usersMetadata with
            {
                Users = _usersMetadata.Users.Select(u => u.Id == userId ? updatedUser : u).ToList()
            };

            await SaveUsersAsync();

            _logger.LogInformation("Granted feed {FeedId} ownership to user {UserId}", feedId, userId);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RevokeFeedOwnershipAsync(string userId, string feedId)
    {
        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null || user.Role != UserRole.FeedOwner)
            {
                return false;
            }

            if (!user.OwnedFeeds.Contains(feedId))
            {
                return true; // Doesn't own this feed anyway
            }

            var updatedUser = user with
            {
                OwnedFeeds = user.OwnedFeeds.Where(f => f != feedId).ToList()
            };

            _usersMetadata = _usersMetadata with
            {
                Users = _usersMetadata.Users.Select(u => u.Id == userId ? updatedUser : u).ToList()
            };

            await SaveUsersAsync();

            _logger.LogInformation("Revoked feed {FeedId} ownership from user {UserId}", feedId, userId);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<bool> ValidatePermissionAsync(User user, string feedId)
    {
        // Admin has access to all feeds
        if (user.Role == UserRole.Admin)
        {
            return Task.FromResult(true);
        }

        // FeedOwner only has access to their owned feeds
        if (user.Role == UserRole.FeedOwner)
        {
            return Task.FromResult(user.OwnedFeeds.Contains(feedId));
        }

        return Task.FromResult(false);
    }

    private async Task SaveUsersAsync()
    {
        // Must be called within lock
        var json = JsonSerializer.Serialize(_usersMetadata, _jsonSerializerOptions);
        await _blobStorage.SaveUsersConfigAsync(json);
    }

    private static string GenerateApiKey()
    {
        // Generate 32 bytes of cryptographically secure random data
        var bytes = RandomNumberGenerator.GetBytes(32);

        // Convert to Base64url (URL-safe, no padding)
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashApiKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
