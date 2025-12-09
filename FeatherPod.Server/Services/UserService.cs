using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Service for managing users and their permissions.
/// Thread-safe singleton service using SemaphoreSlim.
/// </summary>
public sealed class UserService : IUserService, IDisposable
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ILogger<UserService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    private UsersMetadata _usersMetadata = new();

    public UserService(IBlobStorageService blobStorage, ILogger<UserService> logger)
    {
        _blobStorage = blobStorage;
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
                _logger.LogInformation("No users.json found in blob storage. Starting with empty user list.");
                _usersMetadata = new();
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
        // Check if this is a new format key (fp_{userId}_{secret})
        // Note: userId contains only alphanumeric + hyphens (no underscores)
        // Secret is base64url which may contain underscores
        if (apiKey.StartsWith("fp_"))
        {
            var secondUnderscoreIndex = apiKey.IndexOf('_', 3);
            if (secondUnderscoreIndex > 3) // Must have content after "fp_" and before second "_"
            {
                var userId = apiKey[3..secondUnderscoreIndex];

                // O(1) lookup by userId
                await _lock.WaitAsync();
                try
                {
                    var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
                    if (user is { ApiKeySalt: not null })
                    {
                        var keyHash = HashApiKey(apiKey, user.ApiKeySalt);
                        if (user.ApiKeyHash == keyHash)
                        {
                            return user;
                        }
                    }

                    return null;
                }
                finally
                {
                    _lock.Release();
                }
            }
        }

        // Legacy format - O(n) scan for users without salt
        var legacyHash = HashApiKey(apiKey, null);
        await _lock.WaitAsync();
        try
        {
            return _usersMetadata.Users.FirstOrDefault(u => u.ApiKeySalt == null && u.ApiKeyHash == legacyHash);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> CreateUserAsync(User user)
    {
        // Generate API key with salt
        var (apiKey, salt) = GenerateApiKey(user.Id);
        var keyHash = HashApiKey(apiKey, salt);

        await _lock.WaitAsync();
        try
        {
            // Check if user already exists
            if (_usersMetadata.Users.Any(u => u.Id == user.Id))
            {
                throw new InvalidOperationException($"User with ID '{user.Id}' already exists.");
            }

            // Create user with hashed key and salt
            var newUser = user with
            {
                ApiKeyHash = keyHash,
                ApiKeySalt = salt,
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
        var (newApiKey, newSalt) = GenerateApiKey(userId);
        var newKeyHash = HashApiKey(newApiKey, newSalt);

        await _lock.WaitAsync();
        try
        {
            var user = _usersMetadata.Users.FirstOrDefault(u => u.Id == userId);
            if (user == null)
            {
                return null;
            }

            var updatedUser = user with { ApiKeyHash = newKeyHash, ApiKeySalt = newSalt };

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

    private static (string apiKey, string salt) GenerateApiKey(string userId)
    {
        // Generate 128-bit secret (16 bytes -> 22 chars base64url without padding)
        var secretBytes = RandomNumberGenerator.GetBytes(16);
        var secret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Generate 128-bit salt (16 bytes -> base64 for storage)
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);

        var apiKey = $"fp_{userId}_{secret}";

        return (apiKey, salt);
    }

    private static string HashApiKey(string apiKey, string? salt)
    {
        if (salt == null)
        {
            // Legacy unsalted hash (GUID format keys)
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            var hashBytes = SHA256.HashData(bytes);

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        // Extract secret from fp_{userId}_{secret}
        // Note: userId contains only alphanumeric + hyphens (no underscores)
        // Secret is base64url which may contain underscores, so we find the second underscore
        var secondUnderscoreIndex = apiKey.IndexOf('_', 3);
        if (secondUnderscoreIndex == -1 || !apiKey.StartsWith("fp_"))
        {
            // Invalid format - fall back to legacy hash
            var bytes = Encoding.UTF8.GetBytes(apiKey);
            var hashBytes = SHA256.HashData(bytes);

            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var secret = apiKey[(secondUnderscoreIndex + 1)..];
        var saltBytes = Convert.FromBase64String(salt);
        var secretBytes = Encoding.UTF8.GetBytes(secret);

        // Hash(salt + secret)
        var combined = new byte[saltBytes.Length + secretBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
        Buffer.BlockCopy(secretBytes, 0, combined, saltBytes.Length, secretBytes.Length);

        var combinedHashBytes = SHA256.HashData(combined);

        return Convert.ToHexString(combinedHashBytes).ToLowerInvariant();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
