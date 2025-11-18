namespace FeatherPod.Models;

/// <summary>
/// Version information for the FeatherPod API.
/// </summary>
public record VersionInfo
{
    /// <summary>
    /// Version number with git commit SHA (e.g., "0.1.0-396df17").
    /// </summary>
    required public string Version { get; init; }

    /// <summary>
    /// Build date in ISO 8601 format (UTC).
    /// </summary>
    required public string BuildDate { get; init; }

    /// <summary>
    /// Current environment (Development, Production, etc.).
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Target framework (e.g., ".NETCoreApp,Version=v9.0").
    /// </summary>
    public string? TargetFramework { get; init; }
}
