namespace FeatherPod.Models;

/// <summary>
/// Defines the roles a user can have in the system.
/// </summary>
public enum UserRole
{
    /// <summary>
    /// Full system access - can manage users, all feeds, and all episodes.
    /// </summary>
    Admin,

    /// <summary>
    /// Full control over specific feeds they own - can manage episodes and settings,
    /// but cannot grant access to others or manage users.
    /// </summary>
    FeedOwner
}
