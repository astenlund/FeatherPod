namespace FeatherPod.Shared.Models;

/// <summary>
/// Container for all users in the system. Persisted as users.json in blob storage.
/// </summary>
public record UsersMetadata
{
    /// <summary>
    /// List of all users in the system.
    /// </summary>
    public List<User> Users { get; init; } = [];

    /// <summary>
    /// Schema version for future migrations.
    /// </summary>
    public int Version { get; init; } = 1;
}
