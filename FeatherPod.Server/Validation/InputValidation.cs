using System.Text.RegularExpressions;

namespace FeatherPod.Server.Validation;

/// <summary>
/// Provides input validation methods for feed IDs and filenames.
/// </summary>
public static partial class InputValidation
{
    // Feed IDs should be alphanumeric with hyphens only, 1-64 characters
    [GeneratedRegex(@"^[a-zA-Z0-9-]{1,64}$")]
    private static partial Regex FeedIdPattern();

    // User IDs should be alphanumeric with hyphens only (no underscores or dots), 1-64 characters
    // Underscores are reserved as delimiters in the API key format: fp_{userId}_{secret}
    [GeneratedRegex(@"^[a-zA-Z0-9-]{1,64}$")]
    private static partial Regex UserIdPattern();

    // Filenames should not contain path separators or null bytes
    [GeneratedRegex(@"^[^/\\:\*\?""<>\|\x00]+$")]
    private static partial Regex SafeFilenamePattern();

    // Episode IDs: 12-char lowercase hex (SHA256 prefix)
    [GeneratedRegex(@"^[a-f0-9]{12}$")]
    private static partial Regex EpisodeIdPattern();

    // Duration strings: optional days, hours, minutes (e.g., "1h", "30m", "2h30m", "1d")
    [GeneratedRegex(@"^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?$")]
    private static partial Regex DurationPattern();

    /// <summary>
    /// Validates a feed ID for safe use in URLs and file paths.
    /// </summary>
    /// <param name="feedId">The feed ID to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidFeedId(string? feedId)
    {
        return !string.IsNullOrWhiteSpace(feedId) && FeedIdPattern().IsMatch(feedId);
    }

    /// <summary>
    /// Validates a user ID for safe use in API keys.
    /// User IDs can only contain alphanumeric characters and hyphens (no underscores or dots).
    /// </summary>
    /// <param name="userId">The user ID to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidUserId(string? userId)
    {
        return !string.IsNullOrWhiteSpace(userId) && UserIdPattern().IsMatch(userId);
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
    /// Validates an episode ID (12-char lowercase hex, SHA256 prefix).
    /// </summary>
    public static bool IsValidEpisodeId(string? episodeId)
    {
        return !string.IsNullOrWhiteSpace(episodeId) && EpisodeIdPattern().IsMatch(episodeId);
    }

    /// <summary>
    /// Gets the validation error message for an invalid feed ID.
    /// </summary>
    public static string GetFeedIdValidationError(string? feedId)
    {
        return string.IsNullOrWhiteSpace(feedId)
            ? "Feed ID is required"
            : "Feed ID must be 1-64 characters, alphanumeric with hyphens only";
    }

    /// <summary>
    /// Gets the validation error message for an invalid filename.
    /// </summary>
    public static string GetFilenameValidationError(string? filename)
    {
        return string.IsNullOrWhiteSpace(filename)
            ? "Filename is required"
            : "Filename contains invalid characters";
    }

    /// <summary>
    /// Gets the validation error message for an invalid user ID.
    /// </summary>
    public static string GetUserIdValidationError(string? userId)
    {
        return string.IsNullOrWhiteSpace(userId)
            ? "User ID is required"
            : "User ID must be 1-64 characters, alphanumeric with hyphens only (no underscores or dots)";
    }

    /// <summary>
    /// Parse a simple duration string like "1h", "30m", "2h30m", "1d" into a TimeSpan.
    /// </summary>
    public static bool TryParseDuration(string? input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var match = DurationPattern().Match(input.Trim());
        if (!match.Success || match.Length == 0)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].ValueSpan, out var days)) { days = 0; }
        if (!int.TryParse(match.Groups[2].ValueSpan, out var hours)) { hours = 0; }
        if (!int.TryParse(match.Groups[3].ValueSpan, out var minutes)) { minutes = 0; }

        if (days == 0 && hours == 0 && minutes == 0)
        {
            return false;
        }

        result = new TimeSpan(days, hours, minutes, 0);

        return true;
    }
}
