using FeatherPod.Server.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Service for managing users and their permissions.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Loads all users from blob storage.
    /// </summary>
    Task LoadUsersAsync();

    /// <summary>
    /// Gets all active users in the system.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllUsersAsync();

    /// <summary>
    /// Gets a user by their ID.
    /// </summary>
    Task<User?> GetUserByIdAsync(string userId);

    /// <summary>
    /// Gets a user by their API key. Returns null if not found or inactive.
    /// </summary>
    Task<User?> GetUserByApiKeyAsync(string apiKey);

    /// <summary>
    /// Creates a new user with a generated API key.
    /// Returns the plaintext API key (only time it will be visible).
    /// </summary>
    Task<string> CreateUserAsync(User user);

    /// <summary>
    /// Deletes a user (soft delete - sets IsActive to false).
    /// </summary>
    Task<bool> DeleteUserAsync(string userId);

    /// <summary>
    /// Regenerates a user's API key.
    /// Returns the new plaintext API key (only time it will be visible).
    /// </summary>
    Task<string?> RegenerateApiKeyAsync(string userId);

    /// <summary>
    /// Updates a user's last active timestamp.
    /// </summary>
    Task UpdateLastActiveAsync(string userId);

    /// <summary>
    /// Grants feed ownership to a FeedOwner user.
    /// </summary>
    Task<bool> GrantFeedOwnershipAsync(string userId, string feedId);

    /// <summary>
    /// Revokes feed ownership from a FeedOwner user.
    /// </summary>
    Task<bool> RevokeFeedOwnershipAsync(string userId, string feedId);

    /// <summary>
    /// Validates if a user has permission to perform an operation on a feed.
    /// Admin users have access to all feeds.
    /// FeedOwner users only have access to feeds in their OwnedFeeds list.
    /// </summary>
    Task<bool> ValidatePermissionAsync(User user, string feedId);
}
