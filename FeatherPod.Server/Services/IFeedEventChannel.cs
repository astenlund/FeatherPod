using System.Threading.Channels;

namespace FeatherPod.Server.Services;

/// <summary>
/// In-memory pub/sub channel for pushing feed-level events (e.g., new job created)
/// to all connected push page clients for cross-tab/cross-device sync.
/// </summary>
public interface IFeedEventChannel
{
    /// <summary>
    /// Subscribe to events for a feed. Returns a channel reader.
    /// </summary>
    ChannelReader<string> Subscribe(string feedId);

    /// <summary>
    /// Unsubscribe a previously subscribed reader.
    /// </summary>
    void Unsubscribe(string feedId, ChannelReader<string> reader);

    /// <summary>
    /// Publish an event to all subscribers for a feed.
    /// </summary>
    void Publish(string feedId, string eventType);
}
