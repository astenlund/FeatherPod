namespace FeatherPod.Shared;

/// <summary>
/// Single source of truth for the blob layout used by FeatherPod.
/// Referenced by FeatherPod.Server (BlobStorageService) and FeatherPod.Functions
/// (NormalizationFunction, CleanupFunction). Each method returns a blob name
/// (relative to the configured container), not an absolute URL.
/// </summary>
public static class BlobPaths
{
    /// <summary>Root feeds configuration document.</summary>
    public const string FeedsConfig = "feeds.json";

    /// <summary>Root users configuration document.</summary>
    public const string UsersConfig = "users.json";

    /// <summary>All blobs belonging to a feed live under this prefix.</summary>
    public static string FeedPrefix(string feedId) => $"{feedId}/";

    /// <summary>Final, normalized audio blob served by the RSS feed.</summary>
    public static string Audio(string feedId, string fileName) => $"{feedId}/audio/{fileName}";

    /// <summary>Prefix used to enumerate every audio file in a feed.</summary>
    public static string AudioPrefix(string feedId) => $"{feedId}/audio/";

    /// <summary>Pending upload waiting to be picked up by the normalization pipeline.</summary>
    public static string Pending(string feedId, string jobId, string fileName) => $"{feedId}/pending/{jobId}/{fileName}";

    /// <summary>Prefix that scopes a single job's pending blobs (one job may upload multiple files).</summary>
    public static string PendingJobPrefix(string feedId, string jobId) => $"{feedId}/pending/{jobId}/";

    /// <summary>Prefix that scopes every pending blob for a feed (used by cleanup orphan scans).</summary>
    public static string PendingPrefix(string feedId) => $"{feedId}/pending/";

    /// <summary>Feed artwork (PNG). Lives at the feed root so it can be served by URL routing.</summary>
    public static string Icon(string feedId) => $"{feedId}/icon.png";

    /// <summary>Per-feed episode metadata document.</summary>
    public static string EpisodesMetadata(string feedId) => $"{feedId}/episodes.json";

    /// <summary>Per-feed push notification subscription store.</summary>
    public static string PushSubscriptions(string feedId) => $"{feedId}/push-subscriptions.json";

    /// <summary>VTT transcript blob keyed by episode id.</summary>
    public static string Transcript(string feedId, string episodeId) => $"{feedId}/transcripts/{episodeId}.vtt";
}
