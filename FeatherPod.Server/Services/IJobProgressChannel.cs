using System.Threading.Channels;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// In-memory pub/sub channel for pushing real-time job progress updates
/// from the internal endpoint to active SSE connections.
/// </summary>
public interface IJobProgressChannel
{
    /// <summary>
    /// Subscribe to progress updates for a job. Returns a channel reader.
    /// </summary>
    ChannelReader<JobStatusResponse> Subscribe(string jobId);

    /// <summary>
    /// Unsubscribe a previously subscribed reader.
    /// </summary>
    void Unsubscribe(string jobId, ChannelReader<JobStatusResponse> reader);

    /// <summary>
    /// Publish a progress update to all subscribers for a job.
    /// </summary>
    void Publish(string jobId, JobStatusResponse update);
}
