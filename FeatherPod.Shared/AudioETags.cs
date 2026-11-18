using Azure;

namespace FeatherPod.Shared;

/// <summary>
/// The single text form of an audio blob ETag recorded on episodes and jobs. The SDK reports the same blob version quoted from an
/// upload's result and unquoted from a download's response headers, so every recorded ETag goes through <see cref="Format"/> and
/// recorded values compare as plain strings.
/// </summary>
public static class AudioETags
{
    /// <summary>
    /// The ETag in its HTTP header form (quoted), whichever form the SDK returned it in.
    /// </summary>
    public static string Format(ETag eTag) => eTag.ToString("H");
}
