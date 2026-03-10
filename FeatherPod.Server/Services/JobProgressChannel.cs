using System.Collections.Concurrent;
using System.Threading.Channels;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Singleton in-memory pub/sub channel for job progress updates.
/// Uses bounded channels with DropOldest to prevent backpressure from slow consumers.
/// </summary>
public class JobProgressChannel : IJobProgressChannel
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelReader<JobStatusResponse>, ChannelWriter<JobStatusResponse>>> _subscribers = new();

    public ChannelReader<JobStatusResponse> Subscribe(string jobId)
    {
        var channel = Channel.CreateBounded<JobStatusResponse>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var jobSubscribers = _subscribers.GetOrAdd(jobId, _ => new());
        jobSubscribers.TryAdd(channel.Reader, channel.Writer);

        return channel.Reader;
    }

    public void Unsubscribe(string jobId, ChannelReader<JobStatusResponse> reader)
    {
        if (!_subscribers.TryGetValue(jobId, out var jobSubscribers))
        {
            return;
        }

        if (jobSubscribers.TryRemove(reader, out var writer))
        {
            writer.TryComplete();
        }

        // Clean up empty entries
        if (jobSubscribers.IsEmpty)
        {
            _subscribers.TryRemove(jobId, out _);
        }
    }

    public void Publish(string jobId, JobStatusResponse update)
    {
        if (!_subscribers.TryGetValue(jobId, out var jobSubscribers))
        {
            return;
        }

        foreach (var (_, writer) in jobSubscribers)
        {
            writer.TryWrite(update);
        }
    }
}
