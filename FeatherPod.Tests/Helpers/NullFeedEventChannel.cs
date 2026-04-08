using System.Threading.Channels;
using FeatherPod.Server.Services;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// No-op <see cref="IFeedEventChannel"/> for tests that don't observe feed events.
/// </summary>
public sealed class NullFeedEventChannel : IFeedEventChannel
{
    public void Publish(string feedId, string eventType) { }

    public ChannelReader<string> Subscribe(string feedId) => Channel.CreateUnbounded<string>().Reader;

    public void Unsubscribe(string feedId, ChannelReader<string> reader) { }
}
