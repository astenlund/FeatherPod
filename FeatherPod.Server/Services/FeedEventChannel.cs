using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FeatherPod.Server.Services;

/// <summary>
/// Singleton in-memory pub/sub channel for feed-level events.
/// Uses bounded channels with DropOldest to prevent backpressure from slow consumers.
/// </summary>
public class FeedEventChannel : IFeedEventChannel
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelReader<string>, ChannelWriter<string>>> _subscribers = new();

    public ChannelReader<string> Subscribe(string feedId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var feedSubscribers = _subscribers.GetOrAdd(feedId, _ => new());
        feedSubscribers.TryAdd(channel.Reader, channel.Writer);

        return channel.Reader;
    }

    public void Unsubscribe(string feedId, ChannelReader<string> reader)
    {
        if (!_subscribers.TryGetValue(feedId, out var feedSubscribers))
        {
            return;
        }

        if (feedSubscribers.TryRemove(reader, out var writer))
        {
            writer.TryComplete();
        }

        if (feedSubscribers.IsEmpty)
        {
            _subscribers.TryRemove(feedId, out _);
        }
    }

    public void Publish(string feedId, string eventType)
    {
        if (!_subscribers.TryGetValue(feedId, out var feedSubscribers))
        {
            return;
        }

        foreach (var (_, writer) in feedSubscribers)
        {
            writer.TryWrite(eventType);
        }
    }
}
