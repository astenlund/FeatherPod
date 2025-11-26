using System.Text.RegularExpressions;

namespace FeatherPod.Server.Validation;

/// <summary>
/// Provides input validation methods for feed IDs and filenames.
/// </summary>
public static partial class InputValidation
{
    // Feed IDs should be alphanumeric with dots, hyphens, and underscores, 1-64 characters
    [GeneratedRegex(@"^[a-zA-Z0-9._-]{1,64}$")]
    private static partial Regex FeedIdPattern();

    // Filenames should not contain path separators or null bytes
    [GeneratedRegex(@"^[^/\\:\*\?""<>\|\x00]+$")]
    private static partial Regex SafeFilenamePattern();

    /// <summary>
    /// Validates a feed ID for safe use in URLs and file paths.
    /// </summary>
    /// <param name="feedId">The feed ID to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidFeedId(string? feedId)
    {
        if (string.IsNullOrWhiteSpace(feedId))
            return false;

        return FeedIdPattern().IsMatch(feedId);
    }

    /// <summary>
    /// Validates a filename for safe use (no path traversal characters).
    /// </summary>
    /// <param name="filename">The filename to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        // Check for path traversal attempts
        if (filename.Contains("..") || filename.StartsWith('/') || filename.StartsWith('\\'))
            return false;

        return SafeFilenamePattern().IsMatch(filename);
    }

    /// <summary>
    /// Gets the validation error message for an invalid feed ID.
    /// </summary>
    public static string GetFeedIdValidationError(string? feedId)
    {
        if (string.IsNullOrWhiteSpace(feedId))
            return "Feed ID is required";

        return "Feed ID must be 1-64 characters, alphanumeric with dots, hyphens, and underscores only";
    }

    /// <summary>
    /// Gets the validation error message for an invalid filename.
    /// </summary>
    public static string GetFilenameValidationError(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "Filename is required";

        return "Filename contains invalid characters";
    }
}
