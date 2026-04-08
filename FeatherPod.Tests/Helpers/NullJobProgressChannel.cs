using System.Threading.Channels;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// No-op <see cref="IJobProgressChannel"/> for tests that don't observe progress events.
/// </summary>
public sealed class NullJobProgressChannel : IJobProgressChannel
{
    public void Publish(string jobId, JobStatusResponse response) { }

    public ChannelReader<JobStatusResponse> Subscribe(string jobId) => Channel.CreateUnbounded<JobStatusResponse>().Reader;

    public void Unsubscribe(string jobId, ChannelReader<JobStatusResponse> reader) { }
}
