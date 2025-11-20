namespace FeatherPod.Shared.Models;

/// <summary>
/// Represents a user in the FeatherPod system.
/// </summary>
public record User
{
    /// <summary>
    /// Unique username/identifier for the user.
    /// </summary>
    required public string Id { get; init; }

    /// <summary>
    /// Display name for the user.
    /// </summary>
    required public string Name { get; init; }

    /// <summary>
    /// Email address for the user.
    /// </summary>
    required public string Email { get; init; }

    /// <summary>
    /// SHA256 hash of the user's API key. Never store plaintext keys.
    /// </summary>
    required public string ApiKeyHash { get; init; }

    /// <summary>
    /// The user's role in the system (Admin or FeedOwner).
    /// </summary>
    required public UserRole Role { get; init; }

    /// <summary>
    /// List of feed IDs this user owns. Empty for Admin users (they have access to all feeds).
    /// Only populated for FeedOwner users.
    /// </summary>
    public List<string> OwnedFeeds { get; init; } = [];

    /// <summary>
    /// When the user was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Last time the user made an authenticated API request. Null if never used.
    /// </summary>
    public DateTime? LastActive { get; init; }
}
